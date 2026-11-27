// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Projects;

/// <summary>
/// How a request at the gateway is known to be a project's: it carries tags in its metadata (the ledger keeps them with its
/// row), chat:ID for the chat it answers and project:ID for a project its caller names (Code Arena, a script with
/// an API key). As the ledger takes the request in (LedgerImport), the chat's project is its project, as the chat is now;
/// else a project the person may add to. A chat moved to another project takes its requests with it (<see cref="RetagAsync"/>).
/// </summary>
public static class ProjectTags
{
    public const string ChatPrefix = "chat:";
    public const string ProjectPrefix = "project:";

    /// <summary>A request for a chat: its tag goes beside the request's own (the first tag says what part of the app asked).</summary>
    public static void Tag(JsonObject request, Guid conversationId)
    {
        var metadata = request["metadata"] as JsonObject ?? new JsonObject();
        var tags = metadata["tags"] as JsonArray ?? [];
        if (!tags.Any(t => t?.GetValue<string>().StartsWith(ChatPrefix, StringComparison.Ordinal) == true))
        {
            tags.Add(ChatPrefix + conversationId);
        }
        metadata["tags"] = tags;
        request["metadata"] = metadata;
    }

    /// <summary>
    /// Each request's chat and project (by index, the tags of the same row): a chat tag counts when the chat is the person's
    /// own or a room they are in; a project tag when the project is active and the person may add to it. Others are left out.
    /// A request with neither (a key used outside Code Arena) counts to its key's project when the key is a project's, else
    /// to the project an admin put on the person.
    /// </summary>
    public static async Task ResolveAsync(AppDbContext db, AccessService access, List<GatewayRequest> rows, IReadOnlyList<(string? Chat, string? Project)> tags, CancellationToken ct)
    {
        var chatIds = tags.Select(t => Guid.TryParse(t.Chat, out var g) ? g : (Guid?)null).ToList();
        var projectIds = tags.Select(t => Guid.TryParse(t.Project, out var g) ? g : (Guid?)null).ToList();
        // The keys that are a project's (an active one), for the requests that name none.
        var hashes = rows.Select(r => r.KeyHash).OfType<string>().Distinct().ToList();
        var keyProjects = hashes.Count == 0 ? [] : await db.ApiKeys.AsNoTracking().Where(k => hashes.Contains(k.Hash) && k.ProjectId != null)
            .Join(db.Projects.Where(p => p.Status == Project.Active), k => k.ProjectId, p => p.Id, (k, p) => new { k.Hash, p.Id }).ToDictionaryAsync(k => k.Hash, k => k.Id, ct);
        var forced = new Dictionary<Guid, Guid?>();
        var wantedChats = chatIds.OfType<Guid>().Distinct().ToList();
        var chats = await db.Conversations.AsNoTracking().Where(c => wantedChats.Contains(c.Id))
            .Select(c => new { c.Id, c.UserId, c.Kind, c.WorkProjectId }).ToDictionaryAsync(c => c.Id, ct);
        var roomPeople = (await db.RoomMembers.AsNoTracking().Where(r => wantedChats.Contains(r.ConversationId)).Select(r => new { r.ConversationId, r.UserId }).ToListAsync(ct))
            .Select(r => (r.ConversationId, r.UserId)).ToHashSet();
        var wantedProjects = projectIds.OfType<Guid>().Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => wantedProjects.Contains(p.Id) && p.Status == Project.Active).ToDictionaryAsync(p => p.Id, ct);
        var memberships = new Dictionary<Guid, Membership?>();
        var roles = new Dictionary<(Guid, Guid), bool>();
        async Task<Membership?> MembershipAsync(Guid user)
        {
            if (!memberships.TryGetValue(user, out var m))
            {
                m = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user, ct) is { } person ? await access.MembershipAsync(person, ct) : null;
                memberships[user] = m;
            }
            return m;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.UserId is not { } user)
            {
                continue;
            }
            // Named nothing: the key's project, else the one put on the person.
            if (chatIds[i] is null && projectIds[i] is null)
            {
                if (row.KeyHash is { } hash && keyProjects.TryGetValue(hash, out var kp))
                {
                    row.ProjectId = kp;
                }
                else
                {
                    if (!forced.TryGetValue(user, out var put))
                    {
                        put = await MembershipAsync(user) is { } who ? (await ProjectAccess.ForcedAsync(db, user, who, ct)).Cast<Guid?>().FirstOrDefault() : null;
                        forced[user] = put;
                    }
                    row.ProjectId = put;
                }
                continue;
            }
            if (chatIds[i] is { } chat && chats.TryGetValue(chat, out var c) && (c.UserId == user || (c.Kind == Core.Chat.RoomMember.Room && roomPeople.Contains((chat, user)))))
            {
                row.ConversationId = c.Id;
                row.ProjectId = c.WorkProjectId;
                if (c.WorkProjectId is not null)
                {
                    continue;
                }
            }
            if (projectIds[i] is { } pid && projects.TryGetValue(pid, out var project))
            {
                if (!roles.TryGetValue((pid, user), out var may))
                {
                    var m = await MembershipAsync(user);
                    may = m is not null && ProjectAccess.Holds(await ProjectAccess.RoleAsync(db, project, user, m, ct), ProjectMember.Contributor);
                    roles[(pid, user)] = may;
                }
                if (may)
                {
                    row.ProjectId = pid;
                }
            }
        }
    }

    /// <summary>A chat put in a project (or taken out: null): every request of it in the ledger counts there from now on.</summary>
    public static Task<int> RetagAsync(AppDbContext db, Guid conversationId, Guid? projectId, CancellationToken ct) =>
        db.GatewayRequests.Where(r => r.ConversationId == conversationId).ExecuteUpdateAsync(s => s.SetProperty(r => r.ProjectId, projectId), ct);
}
