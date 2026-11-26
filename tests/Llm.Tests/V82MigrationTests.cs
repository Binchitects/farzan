using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has data to v8.2: decks keep their slides and get the built-in look; the Decks tool, now the
/// canvas's slides, is "canvas" wherever it was chosen (once), and its settings, groups' settings and rules are gone;
/// requests imported from LiteLLM whose first token was their end have none; chat answers logged before requests carried
/// their chat are linked to it, and to its project. Scheduled tasks are Automations' workflows of the same id, with their
/// runs, chats, webhooks and plugins' templates, and whoever might schedule may use Automations (whoever was denied, is).
/// A chat's plan in a project is the project's goal and its tasks; Code Arena's plans and those of chats in no project stay.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class V82MigrationTests(AppFixture app)
{
    [Fact]
    public async Task Decks_keep_their_slides_and_the_Decks_tool_is_the_canvas_wherever_it_was_chosen()
    {
        var cs = app.ConnectionStringFor("v82_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_DeckLook", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var (person, deck, chat, both, task, set, rule) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (project, old) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = person, ["UserName"] = "kim", ["NormalizedUserName"] = "KIM" });
            await V61MigrationTests.InsertAsync(conn, "decks", new() { ["Id"] = deck, ["OwnerId"] = person, ["Title"] = "Board", ["Slides"] = """[{"layout":"title","title":"Board"}]""" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = chat, ["UserId"] = person, ["Tools"] = new[] { "web", "decks", "calculator" } });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = both, ["UserId"] = person, ["Tools"] = new[] { "canvas", "decks" }, ["LoadedTools"] = new[] { "decks" } });
            await V61MigrationTests.InsertAsync(conn, "scheduled_tasks", new() { ["Id"] = task, ["UserId"] = person, ["Tools"] = new[] { "decks" } });
            await V61MigrationTests.InsertAsync(conn, "toolsets", new() { ["Id"] = set, ["Name"] = "Writing", ["Tools"] = new[] { "decks", "web" } });
            await V61MigrationTests.InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "decks", ["Enabled"] = true });
            await V61MigrationTests.InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "web", ["Enabled"] = true });
            await V61MigrationTests.InsertAsync(conn, "access_rules", new() { ["Id"] = rule, ["ResourceKind"] = "tool", ["Resource"] = "decks" });
            // Two requests imported from LiteLLM: one not streamed (its first token its end), one streamed; and the app's own.
            await using var ledger = new NpgsqlCommand("""
                insert into gateway.requests (id, started_at, first_token_at, ended_at, duration_ms, surface, call, protocol, credit_kind, model, prompt_tokens,
                  cached_tokens, completion_tokens, usage_estimated, cost, status, http_status, source_id) values
                  ('01a1b3fb-0000-7000-8000-0000000000a1', '2026-10-01 10:00:00Z', '2026-10-01 10:00:09Z', '2026-10-01 10:00:09Z', 9000, 'api', 'chat', 'openai', 'api', 'qwen', 10, 0, 500, false, 0, 'done', 200, 'lite-1'),
                  ('01a1b3fb-0000-7000-8000-0000000000a2', '2026-10-01 10:00:00Z', '2026-10-01 10:00:01Z', '2026-10-01 10:00:09Z', 9000, 'api', 'chat', 'openai', 'api', 'qwen', 10, 0, 500, false, 0, 'done', 200, 'lite-2'),
                  ('01a1b3fb-0000-7000-8000-0000000000a3', '2026-10-01 10:00:00Z', '2026-10-01 10:00:09Z', '2026-10-01 10:00:09Z', 9000, 'api', 'chat', 'openai', 'api', 'qwen', 10, 0, 500, false, 0, 'done', 200, null)
                """, conn);
            await ledger.ExecuteNonQueryAsync();
            // A chat put in a project after its answers, from before requests carried their chat: its answer's request is
            // found by its asker, model and tokens; another request of the same person, with other tokens, is not.
            await V61MigrationTests.InsertAsync(conn, "work_projects", new() { ["Id"] = project, ["Name"] = "MDM", ["OwnerId"] = person });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = old, ["UserId"] = person, ["WorkProjectId"] = project });
            await using var answered = new NpgsqlCommand("""
                update "AspNetUsers" set "Email" = 'kim@example.test' where "Id" = @person;
                insert into chat_messages ("Id", "ConversationId", "Role", "Content", "Status", "Sequence", "CreatedAt", "Model", "PromptTokens", "CompletionTokens")
                  values ('01a1b3fb-0000-7000-8000-0000000000b1', @old, 'user', 'hi', 0, 0, '2026-10-26 17:23:15Z', null, null, null),
                         ('01a1b3fb-0000-7000-8000-0000000000b2', @old, 'assistant', 'hello', 0, 1, '2026-10-26 17:23:16.5Z', 'qwen4', 2090, 67);
                insert into gateway.requests (id, started_at, ended_at, duration_ms, surface, call, protocol, credit_kind, model, person, prompt_tokens,
                  cached_tokens, completion_tokens, usage_estimated, cost, status, http_status, source_id) values
                  ('01a1b3fb-0000-7000-8000-0000000000c1', '2026-10-26 17:23:16.545Z', '2026-10-26 17:23:18Z', 1500, 'chat', 'chat', 'openai', 'chat', 'qwen4', 'KIM@example.test', 2090, 0, 67, false, 0.002, 'done', 200, 'lite-c1'),
                  ('01a1b3fb-0000-7000-8000-0000000000c2', '2026-10-26 17:23:16.6Z', '2026-10-26 17:23:18Z', 1500, 'chat', 'chat', 'openai', 'chat', 'qwen4', 'kim@example.test', 2090, 0, 68, false, 0.002, 'done', 200, 'lite-c2');
                """, conn);
            answered.Parameters.AddWithValue("person", person);
            answered.Parameters.AddWithValue("old", old);
            await answered.ExecuteNonQueryAsync();
        }
        await db.Database.MigrateAsync();

        var kept = await db.Decks.AsNoTracking().SingleAsync(d => d.Id == deck);
        Assert.Equal(("""[{"layout":"title","title":"Board"}]""", "{}"), (kept.Slides, kept.Look));
        Assert.Equal(["web", "calculator", "canvas"], (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == chat)).Tools);
        var twice = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == both);
        Assert.Equal(["canvas"], twice.Tools);
        Assert.Equal(["canvas"], twice.LoadedTools);
        // The task is the workflow of its id now, its tools its answer's.
        Assert.Equal(["canvas"], Llm.Api.Studio.FlowModel.Read((await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task)).Flow).Steps[0].Tools);
        Assert.Equal(["web", "canvas"], (await db.Toolsets.AsNoTracking().SingleAsync(t => t.Id == set)).Tools);
        Assert.Equal(["web"], await db.ToolSettings.AsNoTracking().Select(t => t.ToolId).ToListAsync());
        Assert.False(await db.AccessRules.AsNoTracking().AnyAsync(r => r.Id == rule));
        // An imported answer not streamed has no first token; a streamed one keeps its own; the app's own rows are as they were.
        var firsts = await db.GatewayRequests.AsNoTracking().Where(r => r.Model == "qwen").OrderBy(r => r.Id).Select(r => r.FirstTokenAt).ToListAsync();
        // The old answer's request is the chat's and its project's now; the other is as it was.
        var linked = await db.GatewayRequests.AsNoTracking().Where(r => r.Model == "qwen4").OrderBy(r => r.Id).Select(r => new { r.ConversationId, r.ProjectId }).ToListAsync();
        Assert.Equal((old, project), (linked[0].ConversationId!.Value, linked[0].ProjectId!.Value));
        Assert.Equal((null, null), (linked[1].ConversationId, linked[1].ProjectId));
        Assert.Equal([null, new DateTimeOffset(2026, 10, 1, 10, 0, 1, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 10, 0, 9, TimeSpan.Zero)], firsts);
    }
    [Fact]
    public async Task Scheduled_tasks_become_workflows_of_the_same_id_with_their_runs_chats_webhooks_and_who_may()
    {
        var cs = app.ConnectionStringFor("v82t_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_TasksIntoAutomations", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var (person, chat, digest, review, done, going, role, deny, server) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var next = new DateTimeOffset(2026, 12, 1, 7, 0, 0, TimeSpan.Zero);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("s3cret"u8.ToArray()));
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = person, ["UserName"] = "lee", ["NormalizedUserName"] = "LEE" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = chat, ["UserId"] = person, ["ScheduledTaskId"] = digest });
            // A digest: asked every weekday, one chat carried on, by email and to a Slack webhook (its address encrypted as it was).
            await V61MigrationTests.InsertAsync(conn, "scheduled_tasks", new()
            {
                ["Id"] = digest, ["UserId"] = person, ["Name"] = "Digest", ["Prompt"] = "What is new?", ["Cron"] = "0 8 * * 1-5", ["TimeZone"] = "Europe/Berlin",
                ["Model"] = "qwen", ["Thinking"] = "low", ["Tools"] = new[] { "web" }, ["SameChat"] = true, ["ConversationId"] = chat, ["Email"] = true,
                ["WebhookEncrypted"] = "sealed-by-app-data-key", ["Enabled"] = true, ["NextRunAt"] = next, ["Trigger"] = "schedule",
            });
            // A review GitLab's merge requests start, answered there; its secret as GitLab sends it.
            await V61MigrationTests.InsertAsync(conn, "scheduled_tasks", new()
            {
                ["Id"] = review, ["UserId"] = person, ["Name"] = "Health", ["Prompt"] = "review-repository g/p", ["Cron"] = "", ["Action"] = "review-repository",
                ["Repositories"] = new[] { "g/p" }, ["Ref"] = "main", ["Trigger"] = "gitlab", ["Events"] = new[] { "merge_request" }, ["TriggerSecretHash"] = hash,
                ["ReplyInGitLab"] = true, ["Enabled"] = false,
            });
            await V61MigrationTests.InsertAsync(conn, "scheduled_runs", new() { ["Id"] = done, ["TaskId"] = digest, ["Status"] = "done", ["ConversationId"] = chat, ["Delivery"] = "email sent", ["FinishedAt"] = next });
            await V61MigrationTests.InsertAsync(conn, "scheduled_runs", new() { ["Id"] = going, ["TaskId"] = review, ["Status"] = "running", ["Manual"] = true, ["Link"] = "/code/reviews/1" });
            await V61MigrationTests.InsertAsync(conn, "mcp_servers", new() { ["Id"] = server, ["Name"] = "Pets", ["Url"] = "https://pets.test/v1", ["Plugin"] = "pets" });
            await V61MigrationTests.InsertAsync(conn, "task_templates", new() { ["ServerId"] = server, ["Plugin"] = "pets", ["Name"] = "Feed", ["Prompt"] = "Feed them", ["Cron"] = "0 9 * * *", ["Tools"] = new[] { "web" } });
            await V61MigrationTests.InsertAsync(conn, "access_roles", new() { ["Id"] = role, ["Key"] = "schedulers", ["Name"] = "Schedulers", ["Permissions"] = new[] { "feature.chat", "feature.tasks", "tasks.manage.all" } });
            await V61MigrationTests.InsertAsync(conn, "access_rules", new() { ["Id"] = deny, ["Effect"] = 1, ["SubjectKind"] = 0, ["SubjectId"] = person, ["ResourceKind"] = "feature", ["Resource"] = "feature.tasks", ["Action"] = "use" });
        }
        await db.Database.MigrateAsync();

        var d = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == digest);
        Assert.Equal((person, "Digest", true, next), (d.OwnerId, d.Name, d.Enabled, d.NextRunAt));
        var flow = Llm.Api.Studio.FlowModel.Read(d.Flow);
        Assert.Equal(("schedule", "0 8 * * 1-5", "Europe/Berlin"), (flow.Trigger.Kind, flow.Trigger.Cron, flow.Trigger.TimeZone));
        Assert.Equal(["agent", "notify", "email", "webhook"], flow.Steps.Select(s => s.Kind));
        var ask = flow.Steps[0];
        Assert.Equal(("What is new?", "qwen", "low", true), (ask.Prompt, ask.Model, ask.Thinking, ask.SameChat));
        Assert.Equal(["web"], ask.Tools);
        Assert.Equal("sealed-by-app-data-key", flow.Steps[3].Secret);
        Assert.Contains("{{answer.text}}", flow.Steps[3].Text, StringComparison.Ordinal);
        // Its owner sees only that the webhook is set.
        Assert.Equal(Llm.Api.Studio.FlowModel.Sealed, Llm.Api.Studio.FlowModel.Shown(d.Flow)!["steps"]![3]!["secret"]!.GetValue<string>());

        var h = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == review);
        var hf = Llm.Api.Studio.FlowModel.Read(h.Flow);
        Assert.Equal((hash, false, (DateTimeOffset?)null), (h.SecretHash, h.Enabled, h.NextRunAt));
        Assert.Equal("gitlab", hf.Trigger.Kind);
        Assert.Equal(["merge_request"], hf.Trigger.Events!);
        Assert.Equal(["review", "notify", "comment"], hf.Steps.Select(s => s.Kind));
        Assert.Equal(("review-repository", "g/p", "main"), (hf.Steps[0].Review, hf.Steps[0].Repositories, hf.Steps[0].Ref));
        Assert.Null(Llm.Api.Studio.FlowModel.Check(hf));
        Assert.Null(Llm.Api.Studio.FlowModel.Check(flow));

        // Their runs: the one done with its chat and what it delivered; the one going when it was upgraded, ended.
        var runs = await db.WorkflowRuns.AsNoTracking().Where(r => r.Id == done || r.Id == going).ToDictionaryAsync(r => r.Id);
        Assert.Equal((digest, "schedule", "done"), (runs[done].WorkflowId, runs[done].Trigger, runs[done].Status));
        Assert.Equal((review, "manual", "failed", person), (runs[going].WorkflowId, runs[going].Trigger, runs[going].Status, runs[going].ById!.Value));
        Assert.Contains("upgraded", runs[going].Error, StringComparison.Ordinal);
        Assert.NotNull(runs[going].FinishedAt);
        var steps = await db.WorkflowRunSteps.AsNoTracking().Where(s => s.RunId == done || s.RunId == going).ToDictionaryAsync(s => s.RunId);
        Assert.Equal(($"/chat/{chat}", "email sent", "answer"), (steps[done].Link, steps[done].Output, steps[done].StepId));
        Assert.Equal("/code/reviews/1", steps[going].Link);
        Assert.Equal(digest, (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == chat)).WorkflowId);

        var template = await db.AutomationTemplates.AsNoTracking().SingleAsync(t => t.ServerId == server);
        Assert.Equal(("Feed", "Feed them", "0 9 * * *"), (template.Name, template.Prompt, template.Cron));
        Assert.Equal(["feature.chat", "feature.studio"], (await db.AccessRoles.AsNoTracking().SingleAsync(r => r.Id == role)).Permissions);
        Assert.Equal("feature.studio", (await db.AccessRules.AsNoTracking().SingleAsync(r => r.Id == deny)).Resource);
    }
    [Fact]
    public async Task A_project_chats_plan_becomes_its_goal_and_tasks_done_when_they_were_and_other_plans_stay()
    {
        var cs = app.ConnectionStringFor("v82p_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_PlansIntoProjects", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var (person, project, inProject, alone, plan, codePlan, lonePlan, ticked, oldGoal) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (s1, s2, s3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var changed = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var checkedAt = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = person, ["UserName"] = "mo", ["NormalizedUserName"] = "MO" });
            await V61MigrationTests.InsertAsync(conn, "work_projects", new() { ["Id"] = project, ["Name"] = "Launch", ["OwnerId"] = person });
            await V61MigrationTests.InsertAsync(conn, "work_project_goals", new() { ["Id"] = oldGoal, ["ProjectId"] = project, ["Title"] = "Kick-off", ["Status"] = "done", ["Position"] = 0 });
            await V61MigrationTests.InsertAsync(conn, "work_project_tasks", new() { ["Id"] = ticked, ["ProjectId"] = project, ["Title"] = "Book the room", ["Done"] = true, ["DoneAt"] = checkedAt });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = inProject, ["UserId"] = person, ["WorkProjectId"] = project });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = alone, ["UserId"] = person });
            await V61MigrationTests.InsertAsync(conn, "plans", new() { ["Id"] = plan, ["UserId"] = person, ["ConversationId"] = inProject, ["Title"] = "Go live", ["Goal"] = "Live by December", ["Source"] = "chat", ["CreatedAt"] = changed.AddDays(-5), ["UpdatedAt"] = changed });
            await V61MigrationTests.InsertAsync(conn, "plan_steps", new() { ["Id"] = s1, ["PlanId"] = plan, ["Number"] = 1, ["Title"] = "Design", ["Owner"] = "Ann", ["Status"] = "done", ["CheckedAt"] = checkedAt, ["Start"] = new DateOnly(2026, 11, 1) });
            await V61MigrationTests.InsertAsync(conn, "plan_steps", new() { ["Id"] = s2, ["PlanId"] = plan, ["Number"] = 2, ["Title"] = "Build", ["Status"] = "done", ["DependsOn"] = new[] { 1 } });
            await V61MigrationTests.InsertAsync(conn, "plan_steps", new() { ["Id"] = s3, ["PlanId"] = plan, ["Number"] = 3, ["Title"] = "Ship", ["Status"] = "blocked", ["Notes"] = "Waits for legal.", ["Due"] = new DateOnly(2026, 12, 1), ["DependsOn"] = new[] { 1, 2 } });
            // Code Arena's working plan of a session in the project, and a plan of a chat in no project: both stay plans.
            await V61MigrationTests.InsertAsync(conn, "plans", new() { ["Id"] = codePlan, ["UserId"] = person, ["ConversationId"] = inProject, ["Title"] = "Refactor", ["Source"] = "code" });
            await V61MigrationTests.InsertAsync(conn, "plans", new() { ["Id"] = lonePlan, ["UserId"] = person, ["ConversationId"] = alone, ["Title"] = "Trip", ["Source"] = "chat" });
        }
        await db.Database.MigrateAsync();

        // The task ticked before it had a status is done.
        var was = await db.ProjectTasks.AsNoTracking().SingleAsync(t => t.Id == ticked);
        Assert.Equal(("done", true, checkedAt), (was.Status, was.Done, was.DoneAt!.Value));
        var goal = await db.ProjectGoals.AsNoTracking().SingleAsync(g => g.Id == plan);
        Assert.Equal((project, "Go live", "Live by December", "doing", inProject, 1), (goal.ProjectId, goal.Title, goal.Description, goal.Status, goal.ChatId!.Value, goal.Position));
        Assert.Equal(new DateOnly(2026, 12, 1), goal.DueOn);
        Assert.Null(goal.DoneAt);
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.GoalId == plan).ToDictionaryAsync(t => t.Id);
        Assert.Equal(new[] { s1, s2, s3 }.Order(), tasks.Keys.Order());
        // Done when it was: its check's time, else the plan's last change; never the upgrade's.
        Assert.Equal(("done", true, checkedAt, "Ann", new DateOnly(2026, 11, 1)), (tasks[s1].Status, tasks[s1].Done, tasks[s1].DoneAt!.Value, tasks[s1].Owner!, tasks[s1].StartOn!.Value));
        Assert.Equal(changed, tasks[s2].DoneAt);
        Assert.Equal([s1], tasks[s2].DependsOn);
        Assert.Equal(("blocked", false, "Waits for legal.", new DateOnly(2026, 12, 1)), (tasks[s3].Status, tasks[s3].Done, tasks[s3].Notes!, tasks[s3].DueOn!.Value));
        Assert.Equal([s1, s2], tasks[s3].DependsOn);
        Assert.All(tasks.Values, t => Assert.Equal((project, inProject, person), (t.ProjectId, t.ChatId!.Value, t.CreatedById!.Value)));

        Assert.False(await db.Plans.AnyAsync(x => x.Id == plan));
        Assert.False(await db.PlanSteps.AnyAsync(x => x.PlanId == plan));
        Assert.True(await db.Plans.AnyAsync(x => x.Id == codePlan));
        Assert.True(await db.Plans.AnyAsync(x => x.Id == lonePlan));
    }
}
