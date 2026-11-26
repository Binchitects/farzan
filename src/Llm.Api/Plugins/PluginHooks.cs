using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plugins;

/// <summary>What the plugins' hooks made of a flow: its payload (changed by those granted to change it), or why it stops.</summary>
public sealed record HookOutcome(JsonObject Payload, string? Blocked);

/// <summary>
/// Plugins' hooks in Arena's flows (a message before the model reads it, an answer before the person does, a tool call
/// before it runs): each plugin on that hooks there, is allowed to the person and holds the scope to observe, is called in
/// turn within its hook's deadline. An observe hook never changes the flow. A modify hook's change is kept only with its
/// modify scope granted (audited plugin.hook_modified); a plugin says no only with hooks.block. A hook that fails or runs
/// out of time is skipped (fail open), unless its manifest says failClosed and hooks.block is granted: then the flow stops.
/// </summary>
public sealed class PluginHooks(AppDbContext db, Access.AccessService access, PluginCalls calls, Audit audit, TimeProvider clock)
{
    private static (DateTimeOffset At, List<(McpServer Server, PluginManifest Manifest)> Hooking)? _cache;
    private static readonly Lock CacheLock = new();

    /// <summary>The plugins that hook anywhere, re-read every 15 seconds (on every replica), so a flow with none costs nothing.</summary>
    private async Task<List<(McpServer Server, PluginManifest Manifest)>> HookingAsync(CancellationToken ct)
    {
        lock (CacheLock)
        {
            if (_cache is { } c && clock.GetUtcNow() - c.At < TimeSpan.FromSeconds(15))
            {
                return c.Hooking;
            }
        }
        var servers = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null && s.Url.StartsWith(PluginManifest.PluginScheme)).ToListAsync(ct);
        var off = await db.PluginStates.AsNoTracking().Where(s => s.State != PluginStates.On).Select(s => s.ServerId).ToListAsync(ct);
        var list = new List<(McpServer, PluginManifest)>();
        foreach (var s in servers.Where(s => !off.Contains(s.Id)))
        {
            try
            {
                var m = PluginManifest.Parse(s.Manifest!);
                if (m.Hooks.Length > 0)
                {
                    list.Add((s, m));
                }
            }
            catch (PluginManifest.ManifestException)
            {
                // Not one that can run.
            }
        }
        lock (CacheLock)
        {
            _cache = (clock.GetUtcNow(), list);
        }
        return list;
    }

    /// <summary>A plugin turned on or off, installed or changed: hooks are read again on the next flow.</summary>
    public static void Forget()
    {
        lock (CacheLock)
        {
            _cache = null;
        }
    }

    public async Task<HookOutcome> RunAsync(string hook, AppUser me, Guid? chatId, JsonObject payload, CancellationToken ct)
    {
        var hooking = await HookingAsync(ct);
        if (hooking.Count == 0)
        {
            return new HookOutcome(payload, null);
        }
        var member = await access.MembershipAsync(me, ct);
        foreach (var (server, m) in hooking)
        {
            if (m.Hooks.FirstOrDefault(h => h.On == hook) is not { } h || !Chat.Tools.ToolRegistry.Allowed(member, Chat.Tools.McpServerTool.Prefix + server.Id)
                || !PluginRules.Hooks.TryGetValue(hook, out var need) || !server.PluginPermissions.Contains(PluginRules.GrantKey(need.Observe)))
            {
                continue;
            }
            if (await db.PluginStates.AsNoTracking().AnyAsync(s => s.ServerId == server.Id && s.DevOwnerId != null && s.DevOwnerId != me.Id, ct))
            {
                continue;
            }
            var mayModify = h.Mode == "modify" && server.PluginPermissions.Contains(PluginRules.GrantKey(need.Modify));
            var mayBlock = server.PluginPermissions.Contains(PluginRules.GrantKey("hooks.block"));
            var who = new PluginCallScope(server.Id, server.Plugin!, me.Id, chatId, null, DateTimeOffset.MaxValue, false);
            var r = await calls.CallAsync(server, m, "hook.run", new JsonObject { ["hook"] = hook, ["payload"] = payload.DeepClone() }, who, ct, h.TimeoutMs);
            if (!r.Ok)
            {
                if (h.FailClosed && mayBlock)
                {
                    await audit.WriteAsync("plugin.hook_blocked", server.Plugin, success: false, detail: $"{hook}: it failed ({r.Kind}), and fails closed", actor: me);
                    return new HookOutcome(payload, $"{server.Name} could not check this ({r.Kind}), and nothing goes on without it.");
                }
                continue;
            }
            var action = r.Result?["action"]?.GetValue<string>();
            if (action == "block" && mayBlock)
            {
                var reason = r.Result?["reason"]?.GetValue<string>() is { Length: > 0 } why ? why[..Math.Min(why.Length, 500)] : "it said no";
                await audit.WriteAsync("plugin.hook_blocked", server.Plugin, success: false, detail: $"{hook}: {reason}", actor: me);
                return new HookOutcome(payload, $"{server.Name}: {reason}");
            }
            if (action == "modify" && mayModify && r.Result?["payload"] is JsonObject changed)
            {
                // Only the fields the flow had, changed: a hook cannot add others.
                foreach (var (k, v) in changed)
                {
                    if (payload.ContainsKey(k))
                    {
                        payload[k] = v?.DeepClone();
                    }
                }
                await audit.WriteAsync("plugin.hook_modified", server.Plugin, detail: hook, actor: me);
            }
        }
        return new HookOutcome(payload, null);
    }
}
