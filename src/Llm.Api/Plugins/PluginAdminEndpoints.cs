// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plugins;

public sealed record GrantsRequest(List<string> Granted);

public sealed record StateRequest(bool On, string? Reason = null);

public sealed record ManagersRequest(List<Guid>? People, List<Guid>? Groups);

public sealed record PublisherRequest(string Name, string PublicKeyPem);

/// <summary>
/// Admin → Plugins, plugins v3's part: what each plugin asks for and what is granted (an admin may grant less, and take a
/// grant back), on and off (and on again after it turned itself off), its managers, its worker in the runtime and its
/// logs; and the publishers whose keys are trusted. Each change is audited.
/// </summary>
public static class PluginAdminEndpoints
{
    public static void MapPluginAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/plugins");
        g.MapGet("/{id:guid}/grants", GrantsAsync).RequirePermission(Core.Access.Permissions.PluginsRead);
        g.MapPut("/{id:guid}/grants", SetGrantsAsync).RequirePermission(Core.Access.Permissions.PluginsGrant);
        g.MapGet("/{id:guid}/state", StateAsync).RequirePermission(Core.Access.Permissions.PluginsRead);
        g.MapPut("/{id:guid}/state", SetStateAsync).RequirePermission(Core.Access.Permissions.PluginsEnable);
        g.MapGet("/{id:guid}/managers", ManagersAsync).RequirePermission(Core.Access.Permissions.PluginsRead);
        g.MapPut("/{id:guid}/managers", SetManagersAsync).RequirePermission(Core.Access.Permissions.PluginsConfigure);
        g.MapGet("/{id:guid}/runtime", RuntimeAsync).RequirePermission(Core.Access.Permissions.PluginsRead);
        g.MapGet("/publishers", PublishersAsync).RequirePermission(Core.Access.Permissions.PluginsRead);
        g.MapPost("/publishers", AddPublisherAsync).RequirePermission(Core.Access.Permissions.PluginsGrant);
        g.MapDelete("/publishers/{fingerprint}", RemovePublisherAsync).RequirePermission(Core.Access.Permissions.PluginsGrant);
    }

    private static async Task<(McpServer Server, PluginManifest Manifest)?> FindAsync(AppDbContext db, Guid id, CancellationToken ct, bool tracked = false) =>
        await (tracked ? db.McpServers : db.McpServers.AsNoTracking()).SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is { Manifest: { } text } s
            ? (s, PluginManifest.Parse(text)) : null;

    /// <summary>What the plugin asks for, each in words, and whether it is granted.</summary>
    private static async Task<IResult> GrantsAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not var (server, m))
        {
            return Results.NotFound();
        }
        var asked = PluginContent.Permissions(new PluginPackage(m, server.Manifest!, server.Spec));
        var yes = PluginContent.NeedingYes(asked).ToHashSet();
        return Results.Ok(asked.Select(p => new
        {
            p.Key, p.Text, granted = server.PluginPermissions.Contains(p.Key), needsYes = yes.Contains(p.Key),
            risk = p.Key.StartsWith("scope:", StringComparison.Ordinal) ? PluginRules.Scopes.FirstOrDefault(s => s.Scope == p.Key[6..])?.Risk : p.Key.StartsWith("host:", StringComparison.Ordinal) ? "high" : null,
        }));
    }

    /// <summary>
    /// What is granted now: any of what the plugin asks for, nothing beyond it. A scope taken back is refused from its next
    /// call on (its worker starts again with what it has); the plugin sees what it has (capabilities()).
    /// </summary>
    private static async Task<IResult> SetGrantsAsync(Guid id, GrantsRequest body, AppDbContext db, Audit audit, PluginRuntime runtime, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct, tracked: true) is not var (server, m))
        {
            return Results.NotFound();
        }
        var asked = PluginContent.Permissions(new PluginPackage(m, server.Manifest!, server.Spec)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (body.Granted.FirstOrDefault(k => !asked.Contains(k)) is { } beyond)
        {
            return AuthEndpoints.Problem(400, "grant", $"{server.Name} does not ask for {beyond}: nothing beyond what it asks for is granted.");
        }
        var was = server.PluginPermissions.ToHashSet(StringComparer.Ordinal);
        server.PluginPermissions = [.. body.Granted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        await db.SaveChangesAsync(ct);
        await runtime.StopAsync(server.Plugin!, ct);
        PluginHooks.Forget();
        var given = server.PluginPermissions.Where(k => !was.Contains(k)).ToList();
        var taken = was.Where(k => !server.PluginPermissions.Contains(k)).ToList();
        await audit.WriteAsync("plugin.grants", server.Plugin, detail: $"granted {(given.Count > 0 ? string.Join(", ", given) : "nothing new")}; taken back {(taken.Count > 0 ? string.Join(", ", taken) : "nothing")}");
        return Results.Ok(new { granted = server.PluginPermissions });
    }

    private static async Task<IResult> StateAsync(Guid id, AppDbContext db, PluginCalls calls, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not var (server, _))
        {
            return Results.NotFound();
        }
        var state = await db.PluginStates.AsNoTracking().SingleOrDefaultAsync(s => s.ServerId == id, ct);
        var publisher = state?.Signer is { } fp ? await db.PluginPublishers.AsNoTracking().Where(p => p.Fingerprint == fp).Select(p => p.Name).SingleOrDefaultAsync(ct) : null;
        return Results.Ok(new
        {
            state = state?.State ?? PluginStates.On, reason = state?.Reason, changedAt = state?.ChangedAt, signer = state?.Signer, publisher, untrustedSigner = state?.UntrustedSigner,
            dev = state?.DevOwnerId, health = calls.HealthOf(id),
        });
    }

    /// <summary>On or off, by an admin: on again after it turned itself off starts its count afresh.</summary>
    private static async Task<IResult> SetStateAsync(Guid id, StateRequest body, AppDbContext db, Audit audit, PluginCalls calls, PluginRuntime runtime,
        UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not var (server, _))
        {
            return Results.NotFound();
        }
        var state = await db.PluginStates.SingleOrDefaultAsync(s => s.ServerId == id, ct) ?? db.PluginStates.Add(new PluginState { ServerId = id }).Entity;
        var was = state.State;
        state.State = body.On ? PluginStates.On : PluginStates.Off;
        state.Reason = body.On ? null : body.Reason is { Length: > 0 } r ? r[..Math.Min(r.Length, 500)] : null;
        state.ChangedAt = DateTimeOffset.UtcNow;
        state.ChangedById = (await users.GetUserAsync(principal))?.Id;
        await db.SaveChangesAsync(ct);
        calls.Reset(id);
        PluginHooks.Forget();
        if (!body.On)
        {
            await runtime.StopAsync(server.Plugin!, ct);
        }
        await audit.WriteAsync(body.On ? "plugin.enable" : "plugin.disable", server.Plugin, detail: $"{was} → {state.State}{(state.Reason is null ? "" : $": {state.Reason}")}");
        return Results.Ok(new { state = state.State });
    }

    private static async Task<IResult> ManagersAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var rows = await db.PluginManagers.AsNoTracking().Where(m => m.ServerId == id).ToListAsync(ct);
        var people = await db.Users.AsNoTracking().Where(u => rows.Select(r => r.UserId).Contains(u.Id)).Select(u => new { u.Id, u.UserName, u.DisplayName }).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().Where(g => rows.Select(r => r.GroupId).Contains(g.Id)).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        return Results.Ok(new { people, groups });
    }

    /// <summary>Who may configure this plugin besides those whose role says so: these people, and these groups' people.</summary>
    private static async Task<IResult> SetManagersAsync(Guid id, ManagersRequest body, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not var (server, _))
        {
            return Results.NotFound();
        }
        var people = (body.People ?? []).Distinct().ToList();
        var groups = (body.Groups ?? []).Distinct().ToList();
        if (await db.Users.CountAsync(u => people.Contains(u.Id), ct) != people.Count || await db.Groups.CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
        {
            return AuthEndpoints.Problem(400, "managers", "Someone or some group named is not here.");
        }
        await db.PluginManagers.Where(m => m.ServerId == id).ExecuteDeleteAsync(ct);
        db.PluginManagers.AddRange(people.Select(p => new PluginManager { ServerId = id, UserId = p }).Concat(groups.Select(g => new PluginManager { ServerId = id, GroupId = g })));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin.managers", server.Plugin, detail: $"{people.Count} people, {groups.Count} groups");
        return Results.NoContent();
    }

    /// <summary>Its worker in the runtime now (memory, restarts, calls) and its last log lines.</summary>
    private static async Task<IResult> RuntimeAsync(Guid id, AppDbContext db, PluginRuntime runtime, CancellationToken ct)
    {
        if (await FindAsync(db, id, ct) is not var (server, _))
        {
            return Results.NotFound();
        }
        var status = (await runtime.StatusAsync(ct)).FirstOrDefault(p => p?["plugin"]?.GetValue<string>() == server.Plugin);
        return Results.Ok(new
        {
            available = runtime.Unavailable() is null, why = runtime.Unavailable(), worker = status,
            logs = runtime.Logs.Where(l => l.Plugin == server.Plugin).TakeLast(200).Select(l => l.Line),
        });
    }

    private static async Task<IResult> PublishersAsync(AppDbContext db, CancellationToken ct) =>
        Results.Ok(await db.PluginPublishers.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Fingerprint, p.Name, p.AddedAt }).ToListAsync(ct));

    /// <summary>A publisher whose packages are trusted here: its public key (arena-plugin keygen's .pub.pem), never a private one.</summary>
    private static async Task<IResult> AddPublisherAsync(PublisherRequest body, AppDbContext db, Audit audit, UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (body.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return AuthEndpoints.Problem(400, "key", "That is a private key: give the public one (arena-plugin.key.pub.pem), and keep the private key where it was.");
        }
        var (fingerprint, problem) = PluginFap.PublisherKey(body.PublicKeyPem);
        if (fingerprint is null)
        {
            return AuthEndpoints.Problem(400, "key", problem!);
        }
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return AuthEndpoints.Problem(400, "name", "Name the publisher.");
        }
        if (await db.PluginPublishers.AnyAsync(p => p.Fingerprint == fingerprint, ct))
        {
            return AuthEndpoints.Problem(409, "key", "That key is trusted already.");
        }
        db.PluginPublishers.Add(new PluginPublisher
        {
            Fingerprint = fingerprint, Name = body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 200)], PublicKeyPem = body.PublicKeyPem.Trim(),
            AddedById = (await users.GetUserAsync(principal))?.Id,
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin.publisher_trusted", fingerprint, detail: body.Name.Trim());
        return Results.Ok(new { fingerprint });
    }

    private static async Task<IResult> RemovePublisherAsync(string fingerprint, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.PluginPublishers.Where(p => p.Fingerprint == fingerprint).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }
        await audit.WriteAsync("plugin.publisher_removed", fingerprint);
        return Results.NoContent();
    }
}
