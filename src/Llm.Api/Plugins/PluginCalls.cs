using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plugins;

/// <summary>
/// Every call into a plugin's server code goes through here, whoever makes it (a model's tool call, a route, a frame, a
/// hook, an event, a job): the plugin must be on, the call goes to the runtime with its deadline, and what came of it is
/// counted by the plugin's breaker. Five of the plugin's own failures in a row, or ten of its last twenty within ten
/// minutes (the framework's rule), turn it off by itself: its state says why, its admins hear of it on the bell, the
/// audit log has it (plugin.auto_disabled), its worker is stopped, and nothing calls it until an admin turns it on.
/// A failure that is not the plugin's (the runtime down, the person not allowed) is not counted.
/// </summary>
public sealed class PluginCalls(PluginRuntime runtime, IServiceScopeFactory scopes, TimeProvider clock)
{
    public const int InARow = 5, Window = 20, InWindow = 10;
    public static readonly TimeSpan WindowTime = TimeSpan.FromMinutes(10);

    private sealed class Health
    {
        public int InARow;
        public readonly Queue<(DateTimeOffset At, bool Failed)> Recent = new();
        public string? LastError;
        public DateTimeOffset? LastErrorAt;
        public long Calls, Failures;
    }

    private readonly ConcurrentDictionary<Guid, Health> _health = new();

    /// <summary>What the breaker has seen of a plugin, on this replica: calls, failures, in a row, the last error.</summary>
    public object HealthOf(Guid serverId)
    {
        var h = _health.GetOrAdd(serverId, _ => new Health());
        lock (h)
        {
            return new { calls = h.Calls, failures = h.Failures, inARow = h.InARow, lastError = h.LastError, lastErrorAt = h.LastErrorAt };
        }
    }

    /// <summary>Whether the plugin is on (no state row: on, as plugins installed before 8.3 are), or why not.</summary>
    public static async Task<string?> OffAsync(AppDbContext db, Guid serverId, CancellationToken ct)
    {
        var state = await db.PluginStates.AsNoTracking().SingleOrDefaultAsync(s => s.ServerId == serverId, ct);
        return state is null || state.State == PluginStates.On ? null
            : state.State == PluginStates.Tripped ? $"It was turned off by itself after failing: {state.Reason}"
            : $"An admin turned it off{(state.Reason is { Length: > 0 } r ? $": {r}" : "")}.";
    }

    public async Task<PluginCallResult> CallAsync(McpServer server, PluginManifest manifest, string method, JsonObject args, PluginCallScope who, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            if (await OffAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), server.Id, ct) is { } off)
            {
                return new PluginCallResult(null, "off", $"{server.Name} is off. {off}");
            }
        }
        var r = await runtime.CallAsync(server, manifest, method, args, who, ct);
        await RecordAsync(server, r, method, ct);
        return r;
    }

    private async Task RecordAsync(McpServer server, PluginCallResult r, string method, CancellationToken ct)
    {
        if (!r.Ok && !r.PluginsFault)
        {
            return;
        }
        var h = _health.GetOrAdd(server.Id, _ => new Health());
        string? trip = null;
        lock (h)
        {
            var now = clock.GetUtcNow();
            h.Calls++;
            h.Recent.Enqueue((now, !r.Ok));
            while (h.Recent.Count > Window || (h.Recent.TryPeek(out var first) && now - first.At > WindowTime))
            {
                h.Recent.Dequeue();
            }
            if (r.Ok)
            {
                h.InARow = 0;
                return;
            }
            h.Failures++;
            h.InARow++;
            h.LastError = $"{method}: {r.Kind}: {r.Message}";
            h.LastErrorAt = now;
            var failed = h.Recent.Count(x => x.Failed);
            if (h.InARow >= InARow)
            {
                trip = $"{h.InARow} failures in a row, the last a {r.Kind} ({method}): {r.Message}";
            }
            else if (failed >= InWindow)
            {
                trip = $"{failed} of its last {h.Recent.Count} calls failed, the last a {r.Kind} ({method}): {r.Message}";
            }
            if (trip is not null)
            {
                h.InARow = 0;
                h.Recent.Clear();
            }
        }
        if (trip is not null)
        {
            await TripAsync(server, trip, ct);
        }
    }

    /// <summary>The breaker opens: the plugin off by itself, stopped, its admins told, audited.</summary>
    private async Task TripAsync(McpServer server, string why, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.PluginStates.SingleOrDefaultAsync(s => s.ServerId == server.Id, ct);
        if (state is { State: PluginStates.Tripped or PluginStates.Off })
        {
            return;
        }
        if (state is null)
        {
            state = new PluginState { ServerId = server.Id };
            db.PluginStates.Add(state);
        }
        state.State = PluginStates.Tripped;
        state.Reason = why.Length > 2000 ? why[..2000] : why;
        state.ChangedAt = clock.GetUtcNow();
        state.ChangedById = null;
        await db.SaveChangesAsync(ct);
        await runtime.StopAsync(server.Plugin!, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<Audit>().WriteAsync("plugin.auto_disabled", server.Plugin, success: false, detail: state.Reason);
        await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().ToAdminsAsync(
            new Notifications.News("plugin", $"{server.Name} was turned off", $"It failed again and again: {state.Reason} Turn it on again in Admin → Plugins once it is fixed.",
                "/admin/plugins", $"plugin-off:{server.Id}"), CancellationToken.None);
    }

    /// <summary>An admin turned the plugin on again: its count starts afresh.</summary>
    public void Reset(Guid serverId) => _health.TryRemove(serverId, out _);
}
