// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using Llm.Api.Access;
using Llm.Api.ArenaMcp;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Operations;
using Llm.Api.Schedules;
using Llm.Api.Studio;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Studio;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code;

/// <summary>
/// The person's agents (Automations' workflows), for Code Arena with their key, as the pages have them: listed, run, and
/// each run followed; and those of theirs that run by themselves (a schedule, a webhook, GitLab) as its scheduled tasks.
/// The same rules as the pages: an agent is the person's or shared with them, a scheduled one their own, as their role allows.
/// </summary>
public static class CodeArenaAutomation
{
    public static void MapCodeArenaAutomation(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/code-arena").AllowAnonymous("Code Arena: the person's API key is checked on each call.");
        g.MapGet("/agents", AgentsAsync);
        g.MapPost("/agents/{id:guid}/run", RunAgentAsync);
        g.MapGet("/agents/runs/{runId:guid}", AgentRunAsync);
        g.MapGet("/scheduled", ScheduledAsync);
        g.MapPost("/scheduled/{id:guid}/run", RunScheduledAsync);
        g.MapGet("/scheduled/{id:guid}/runs", ScheduledRunsAsync);
    }

    private static async Task<(AppUser? Me, IResult? Refusal)> AgentsCallerAsync(HttpContext http, McpPeople people, AccessService access, Features features)
    {
        var (me, refusal) = await CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return (null, refusal);
        }
        if (!features.IsOn("agents"))
        {
            return (null, AuthEndpoints.Problem(404, "feature-off", "Automations is not installed in this Arena."));
        }
        return (await access.MembershipAsync(me, http.RequestAborted)).Has(Core.Access.Permissions.Studio)
            ? (me, null)
            : (null, AuthEndpoints.Problem(403, "studio", "Your role does not include Automations."));
    }

    /// <summary>The agents the person may run: theirs, and those shared with their groups or the company (on).</summary>
    private static async Task<IResult> AgentsAsync(HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        var list = await db.Workflows.AsNoTracking()
            .Where(w => w.OwnerId == me.Id || (w.Enabled && (w.Reach == Workflow.Company || (w.Reach == Workflow.Groups && w.GroupIds.Any(g => groups.Contains(g))))))
            .OrderByDescending(w => w.UpdatedAt).Take(300).ToListAsync(ct);
        var owners = list.Select(w => w.OwnerId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
        return Results.Ok(list.Select(w => StudioEndpoints.Brief(w, me.Id, names.GetValueOrDefault(w.OwnerId))));
    }

    private static async Task<IResult> RunAgentAsync(Guid id, RunRequest body, HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db, StudioRuns runs)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var (run, problem) = await StudioEndpoints.StartRunAsync(id, body, me, db, access, runs, http.RequestAborted);
        return problem ?? Results.Accepted($"/api/code-arena/agents/runs/{run}", new { id = run });
    }

    private static async Task<IResult> AgentRunAsync(Guid runId, HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        return await StudioEndpoints.DetailAsync(runId, me, db, http.RequestAborted) is { } detail ? Results.Ok(detail) : Results.NotFound();
    }

    /// <summary>Workflows that run by themselves (a schedule, a webhook, GitLab): what scheduled tasks were, now Automations'.</summary>
    private static readonly string[] Unattended = ["schedule", "webhook", "gitlab"];

    /// <summary>A run as Code Arena reads a scheduled task's: its chat or review, and whether it was started by hand.</summary>
    private static object RunView(WorkflowRun r, string? link) => new
    {
        r.Id, r.StartedAt, r.FinishedAt, r.Status, conversationId = link is { } l && l.StartsWith("/chat/", StringComparison.Ordinal) && Guid.TryParse(l[6..], out var c) ? c : (Guid?)null,
        link, r.Error, manual = r.Trigger == "manual", delivery = (string?)null,
    };

    /// <summary>The first link a run's steps made (its chat, its review).</summary>
    private static async Task<Dictionary<Guid, string?>> LinksAsync(AppDbContext db, IReadOnlyCollection<Guid> runs, CancellationToken ct) =>
        (await db.WorkflowRunSteps.AsNoTracking().Where(s => runs.Contains(s.RunId) && s.Link != null).Select(s => new { s.RunId, s.Order, s.Link }).ToListAsync(ct))
            .GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).First().Link);

    /// <summary>The person's workflows that run by themselves, as their scheduled tasks were shown: each with its next runs and how its last went.</summary>
    private static async Task<IResult> ScheduledAsync(HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db, TimeProvider clock)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var mine = (await db.Workflows.AsNoTracking().Where(w => w.OwnerId == me.Id).OrderBy(w => w.CreatedAt).ToListAsync(ct))
            .Select(w => (W: w, F: FlowModel.Read(w.Flow))).Where(x => Unattended.Contains(x.F.Trigger.Kind)).ToList();
        var ids = mine.Select(x => x.W.Id).ToList();
        var last = (await db.WorkflowRuns.AsNoTracking().Where(r => ids.Contains(r.WorkflowId)).GroupBy(r => r.WorkflowId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First()).ToListAsync(ct)).ToDictionary(r => r.WorkflowId);
        var links = await LinksAsync(db, [.. last.Values.Select(r => r.Id)], ct);
        return Results.Ok(mine.Select(x =>
        {
            var t = x.F.Trigger;
            var run = last.GetValueOrDefault(x.W.Id);
            return new
            {
                x.W.Id, x.W.Name, prompt = FlowModel.All(x.F.Steps).FirstOrDefault(s => s.Prompt is not null)?.Prompt ?? x.W.Description,
                cron = t.Kind == "schedule" ? t.Cron : null, timeZone = t.TimeZone, x.W.Enabled, x.W.NextRunAt, x.W.LastRunAt,
                running = run?.Status is WorkflowRun.Running or WorkflowRun.Waiting,
                nextRuns = x.W.Enabled && t.Kind == "schedule" && Cron.Parse(t.Cron).Cron is { } cron ? cron.NextRuns(now, Models.Hours.Zone(t.TimeZone).Zone, 3) : [],
                lastRun = run is null ? null : RunView(run, links.GetValueOrDefault(run.Id)),
                trigger = t.Kind, events = t.Events ?? [],
            };
        }));
    }

    private static async Task<IResult> RunScheduledAsync(Guid id, HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db, StudioRuns runs)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id && w.OwnerId == me.Id, ct) is not { } w || !Unattended.Contains(FlowModel.Read(w.Flow).Trigger.Kind))
        {
            return Results.NotFound();
        }
        if (await db.WorkflowRuns.AnyAsync(r => r.WorkflowId == id && (r.Status == WorkflowRun.Running || r.Status == WorkflowRun.Waiting), ct))
        {
            return AuthEndpoints.Problem(409, "running", "It is running now; wait for this run to end.");
        }
        await runs.StartAsync(w, "manual", new System.Text.Json.Nodes.JsonObject { ["text"] = "" }, me.Id, null, ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ScheduledRunsAsync(Guid id, HttpContext http, McpPeople people, AccessService access, Features features, AppDbContext db)
    {
        var (me, refusal) = await AgentsCallerAsync(http, people, access, features);
        if (me is null)
        {
            return refusal!;
        }
        var ct = http.RequestAborted;
        if (!await db.Workflows.AnyAsync(w => w.Id == id && w.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        var list = await db.WorkflowRuns.AsNoTracking().Where(r => r.WorkflowId == id).OrderByDescending(r => r.StartedAt).Take(50).ToListAsync(ct);
        var links = await LinksAsync(db, [.. list.Select(r => r.Id)], ct);
        return Results.Ok(list.Select(r => RunView(r, links.GetValueOrDefault(r.Id))));
    }
}
