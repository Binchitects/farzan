using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plugins;

/// <summary>
/// Events for plugins: when something happens in Arena that plugins may hear (a chat or a message made, a project, a
/// file, a person, a group's member, a plugin installed or set), each plugin on that listens to it, and holds the scope
/// the event is about, gets an event kept in plugin_events until delivered (at least once: an id to drop repeats).
/// </summary>
public sealed class PluginEvents(AppDbContext db, TimeProvider clock)
{
    /// <summary>Keeps the event for every plugin that hears it; delivered by <see cref="PluginWork"/>.</summary>
    /// <param name="person">The person it is about; a plugin hears it only when it is allowed to them (checked at delivery).</param>
    public async Task EmitAsync(string type, JsonObject data, Guid? person, CancellationToken ct)
    {
        if (!PluginRules.Events.TryGetValue(type, out var need))
        {
            throw new ArgumentException($"{type} is not an event.", nameof(type));
        }
        var listening = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null && s.Url.StartsWith(PluginManifest.PluginScheme)).ToListAsync(ct);
        var off = await db.PluginStates.AsNoTracking().Where(s => s.State != PluginStates.On).Select(s => s.ServerId).ToListAsync(ct);
        foreach (var s in listening.Where(s => !off.Contains(s.Id)))
        {
            PluginManifest m;
            try
            {
                m = PluginManifest.Parse(s.Manifest!);
            }
            catch (PluginManifest.ManifestException)
            {
                continue;
            }
            if (!m.Events.Contains(type) || (need is not null && !s.PluginPermissions.Contains(PluginRules.GrantKey(need))))
            {
                continue;
            }
            db.PluginEvents.Add(new PluginEvent { ServerId = s.Id, Type = type, DataJson = data.ToJsonString(), PersonId = person, At = clock.GetUtcNow(), DueAt = clock.GetUtcNow() });
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Plugins' work in the background, on every replica and claimed so it runs once: their jobs on their schedules (a due
/// job claimed by moving its next time on, as Automations' are), and their events delivered (each claimed by moving its
/// due time on; tried again with a longer wait, given up after eight tries). Both go through <see cref="PluginCalls"/>,
/// so a plugin's breaker counts them and an off plugin gets none.
/// </summary>
public sealed partial class PluginWork(IServiceScopeFactory scopes, PluginCalls calls, PluginRuntime runtime, TimeProvider clock, ILogger<PluginWork> log) : BackgroundService
{
    public const int MostTries = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            if (runtime.Unavailable() is not null)
            {
                continue;
            }
            try
            {
                await JobsAsync(stoppingToken);
                await EventsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WorkFailed(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugins' background work failed")]
    private static partial void WorkFailed(ILogger logger, Exception ex);

    /// <summary>Each plugin's jobs: planned when first seen, run when due (claimed, so once), planned again.</summary>
    public async Task<int> JobsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();
        var servers = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null && s.Url.StartsWith(PluginManifest.PluginScheme)).ToListAsync(ct);
        var states = await db.PluginStates.AsNoTracking().ToDictionaryAsync(s => s.ServerId, ct);
        var ran = 0;
        foreach (var server in servers)
        {
            if (states.TryGetValue(server.Id, out var st) && (st.State != PluginStates.On || st.DevOwnerId is not null))
            {
                continue;
            }
            PluginManifest m;
            try
            {
                m = PluginManifest.Parse(server.Manifest!);
            }
            catch (PluginManifest.ManifestException)
            {
                continue;
            }
            var planned = await db.PluginJobs.AsNoTracking().Where(j => j.ServerId == server.Id).ToDictionaryAsync(j => j.Name, ct);
            foreach (var job in m.Jobs)
            {
                var cron = Schedules.Cron.Parse(job.Cron).Cron!;
                if (!planned.TryGetValue(job.Name, out var row))
                {
                    db.PluginJobs.Add(new PluginJob { ServerId = server.Id, Name = job.Name, NextAt = cron.Next(now, TimeZoneInfo.Utc) ?? now.AddYears(10) });
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                if (row.NextAt > now)
                {
                    continue;
                }
                var next = cron.Next(now, TimeZoneInfo.Utc) ?? now.AddYears(10);
                if (await db.PluginJobs.Where(j => j.ServerId == server.Id && j.Name == job.Name && j.NextAt == row.NextAt)
                    .ExecuteUpdateAsync(u => u.SetProperty(j => j.NextAt, next).SetProperty(j => j.LastAt, now), ct) != 1)
                {
                    continue; // another replica took it
                }
                var r = await calls.CallAsync(server, m, "job.run", new JsonObject
                {
                    ["name"] = job.Name, ["runId"] = Guid.CreateVersion7().ToString(), ["scheduledAt"] = row.NextAt.UtcDateTime.ToString("O"),
                }, new PluginCallScope(server.Id, server.Plugin!, null, null, null, DateTimeOffset.MaxValue, false), ct);
                await db.PluginJobs.Where(j => j.ServerId == server.Id && j.Name == job.Name)
                    .ExecuteUpdateAsync(u => u.SetProperty(j => j.LastError, r.Ok ? null : $"{r.Kind}: {r.Message}"), ct);
                ran++;
            }
        }
        return ran;
    }

    /// <summary>The events due: each claimed, delivered as its person (when the plugin is allowed to them), kept until done.</summary>
    public async Task<int> EventsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<Access.AccessService>();
        var now = clock.GetUtcNow();
        var due = await db.PluginEvents.AsNoTracking().Where(e => e.DueAt <= now).OrderBy(e => e.DueAt).Take(50).ToListAsync(ct);
        var delivered = 0;
        foreach (var e in due)
        {
            // Claimed: its due time moved on by its wait, so another replica leaves it, and a failure tries again then.
            var wait = TimeSpan.FromSeconds(Math.Min(3600, 5 * Math.Pow(3, e.Tries)));
            if (await db.PluginEvents.Where(x => x.Id == e.Id && x.DueAt == e.DueAt).ExecuteUpdateAsync(u => u.SetProperty(x => x.DueAt, now + wait).SetProperty(x => x.Tries, e.Tries + 1), ct) != 1)
            {
                continue;
            }
            var server = await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == e.ServerId, ct);
            var state = await db.PluginStates.AsNoTracking().SingleOrDefaultAsync(s => s.ServerId == e.ServerId, ct);
            if (server?.Manifest is null || state is { State: not PluginStates.On })
            {
                // An off plugin's events wait for it (an admin turning it on gets them), unless they are old.
                if (server is null || now - e.At > TimeSpan.FromDays(1))
                {
                    await db.PluginEvents.Where(x => x.Id == e.Id).ExecuteDeleteAsync(ct);
                }
                continue;
            }
            Guid? person = null;
            if (e.PersonId is { } pid && await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == pid && !u.IsDisabled, ct) is { } u
                && Chat.Tools.ToolRegistry.Allowed(await access.MembershipAsync(u, ct), Chat.Tools.McpServerTool.Prefix + server.Id)
                && (state?.DevOwnerId is null || state.DevOwnerId == pid))
            {
                person = pid;
            }
            if (e.PersonId is not null && person is null)
            {
                // About someone the plugin is not allowed to (or a dev install's other people): never delivered.
                await db.PluginEvents.Where(x => x.Id == e.Id).ExecuteDeleteAsync(ct);
                continue;
            }
            var r = await calls.CallAsync(server, PluginManifest.Parse(server.Manifest), "event.deliver", new JsonObject
            {
                ["id"] = e.Id.ToString(), ["type"] = e.Type, ["at"] = e.At.UtcDateTime.ToString("O"), ["data"] = JsonNode.Parse(e.DataJson),
            }, new PluginCallScope(server.Id, server.Plugin!, person, null, null, DateTimeOffset.MaxValue, state?.DevOwnerId is not null), ct);
            if (r.Ok || e.Tries + 1 >= MostTries)
            {
                await db.PluginEvents.Where(x => x.Id == e.Id).ExecuteDeleteAsync(ct);
                delivered += r.Ok ? 1 : 0;
            }
            else
            {
                await db.PluginEvents.Where(x => x.Id == e.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastError, $"{r.Kind}: {r.Message}"), ct);
            }
        }
        return delivered;
    }
}
