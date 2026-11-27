// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Plans;
using Llm.Core.Plans;

namespace Llm.Tests;

/// <summary>
/// Plans: made and kept by the chat's Plans tool, read and moved along on the page; a room's are its people's. A chat in a
/// project plans in the project: its plan moves there when it joins, a goal and its tasks, kept with the project's tools.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PlanTests(AppFixture app)
{
    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin)
    {
        var user = "pl" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!));
    }

    private static async Task<JsonElement> CallAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        var events = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement).ToList();
        return events.Single(e => e.GetProperty("type").GetString() == "tool_result");
    }

    [Fact]
    public async Task The_chat_makes_a_plan_keeps_it_up_to_date_and_its_room_moves_it_along()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, owner) = await PersonAsync(admin);
        var (otherId, other) = await PersonAsync(admin);
        var (_, outsider) = await PersonAsync(admin);
        var room = (await owner.JsonAsync(await owner.PostAsync("/api/rooms", new { title = "Launch", userIds = new[] { otherId } }))).GetProperty("id").GetGuid();
        await owner.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{room}", UriKind.Relative), new { tools = new[] { "plans" } });

        var made = await CallAsync(owner, room, """@Arena plan it [call plan_make {"title": "Launch", "goal": "Live by December", "steps": [{"title": "Design", "owner": "Ann", "start": "2026-11-01", "due": "2026-11-10"}, {"title": "Build", "owner": "Ben", "start": "2026-11-05", "due": "2026-11-25", "depends_on": [1]}]}]""");
        Assert.False(made.GetProperty("isError").GetBoolean(), made.GetProperty("text").GetString());
        var plan = JsonDocument.Parse(made.GetProperty("text").GetString()!).RootElement;
        var id = plan.GetProperty("id").GetGuid();
        Assert.Equal(["Design", "Build"], plan.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        // Build starts before Design, which it waits for, is due.
        Assert.Contains(plan.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Step 2 starts 2026-11-05, before step 1", StringComparison.Ordinal));

        // The room's people see it and move it along; nobody else does.
        var seen = await other.JsonAsync(await other.GetAsync($"/api/plans/{id}"));
        Assert.Equal("Live by December", seen.GetProperty("goal").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await other.Http.PatchAsJsonAsync(new Uri($"/api/plans/{id}/steps/1", UriKind.Relative), new { status = "done" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/plans/{id}"));
        Assert.Equal("done", (await other.JsonAsync(await other.GetAsync($"/api/plans/{id}"))).GetProperty("steps")[0].GetProperty("status").GetString());

        // The chat reads it as people left it, and changes it; a circle is refused.
        var shown = JsonDocument.Parse((await CallAsync(owner, room, """@Arena where are we [call plan_show {}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Equal("done", shown.GetProperty("steps")[0].GetProperty("status").GetString());
        var circle = await CallAsync(owner, room, """@Arena fix [call plan_change {"steps": [{"number": 1, "depends_on": [2]}]}]""");
        Assert.True(circle.GetProperty("isError").GetBoolean());
        Assert.Contains("in a circle", circle.GetProperty("text").GetString(), StringComparison.Ordinal);
        // Refused, nothing of it is kept.
        Assert.Empty((await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("steps")[0].GetProperty("dependsOn").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await other.Http.PatchAsJsonAsync(new Uri($"/api/plans/{id}/steps/2", UriKind.Relative), new { due = "2026-10-01" }));

        // A step added to the plan as it is kept, on the board and by the chat.
        var added = await other.PostAsync($"/api/plans/{id}/steps", new { title = "Launch party" });
        await StatusAssert.Is(HttpStatusCode.OK, added);
        Assert.Equal([1, 2, 3], (await other.JsonAsync(added)).GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("number").GetInt32()));
        var more = await CallAsync(owner, room, """@Arena add it [call plan_change {"steps": [{"title": "Press release", "check": "Published on the site", "depends_on": [3]}]}]""");
        Assert.False(more.GetProperty("isError").GetBoolean(), more.GetProperty("text").GetString());
        var four = (await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("steps")[3];
        Assert.Equal(("Press release", "Published on the site"), (four.GetProperty("title").GetString(), four.GetProperty("check").GetString()));
    }

    [Fact]
    public async Task The_chat_proposes_a_plan_of_its_own_work_and_the_person_approves_it_changes_a_step_or_rejects_it()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, owner) = await PersonAsync(admin);
        var (_, outsider) = await PersonAsync(admin);
        var chat = (await owner.JsonAsync(await owner.PostAsync("/api/chat/conversations", new { tools = new[] { "plans" } }))).GetProperty("id").GetGuid();
        var made = await CallAsync(owner, chat, """research it [call plan_make {"title": "Market study", "propose": true, "steps": [{"title": "Find the competitors", "check": "Five named, with prices"}, {"title": "Write the summary"}]}]""");
        Assert.False(made.GetProperty("isError").GetBoolean(), made.GetProperty("text").GetString());
        var id = JsonDocument.Parse(made.GetProperty("text").GetString()!).RootElement.GetProperty("id").GetGuid();
        Assert.Equal("proposed", (await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("review").GetString());

        // Someone who may not open the chat reviews nothing; a change needs the step and what.
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.PostAsync($"/api/plans/{id}/review", new { action = "approve" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await owner.PostAsync($"/api/plans/{id}/review", new { action = "change", step = 1 }));
        var change = await owner.JsonAsync(await owner.PostAsync($"/api/plans/{id}/review", new { action = "change", step = 1, text = "only those selling in Europe" }));
        Assert.Equal("Change step 1 of the plan (Find the competitors): only those selling in Europe. Then propose the plan again.", change.GetProperty("message").GetString());

        var approved = await owner.JsonAsync(await owner.PostAsync($"/api/plans/{id}/review", new { action = "approve" }));
        Assert.StartsWith("I approve the plan \"Market study\". Carry it out now, step by step", approved.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("approved", (await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("review").GetString());
        await StatusAssert.Is(HttpStatusCode.Conflict, await owner.PostAsync($"/api/plans/{id}/review", new { action = "approve" }));

        // Rejected, a proposed plan goes.
        var other = JsonDocument.Parse((await CallAsync(owner, chat, """another [call plan_make {"title": "Draft", "propose": true, "steps": [{"title": "Outline"}]}]""")).GetProperty("text").GetString()!).RootElement.GetProperty("id").GetGuid();
        var rejected = await owner.JsonAsync(await owner.PostAsync($"/api/plans/{other}/review", new { action = "reject", text = "not now" }));
        Assert.Equal("I reject the plan \"Draft\": not now. Do not carry it out.", rejected.GetProperty("message").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await owner.GetAsync($"/api/plans/{other}"));
    }

    [Fact]
    public void Steps_are_numbered_checked_and_removed_with_what_waited_for_them()
    {
        var plan = new Plan { Title = "T" };
        PlanBook.Apply(plan, [new StepChange(Title: "A"), new StepChange(Title: "B", DependsOn: [1]), new StepChange(Title: "C", DependsOn: [1, 2])]);
        Assert.Equal([1, 2, 3], plan.Steps.Select(s => s.Number));
        PlanBook.Apply(plan, [new StepChange(Number: 1, Remove: true)]);
        Assert.Equal([2], plan.Steps.Single(s => s.Number == 3).DependsOn);
        Assert.Throws<PlanException>(() => PlanBook.Apply(plan, [new StepChange(Number: 2, Start: "next week")]));
        Assert.Throws<PlanException>(() => PlanBook.Apply(plan, [new StepChange(Number: 2, DependsOn: [9])]));
        Assert.Throws<PlanException>(() => PlanBook.Apply(plan, [new StepChange(Title: "")]));
    }
    [Fact]
    public async Task A_chats_plan_moves_into_the_project_it_joins_as_a_goal_and_its_tasks_kept_there_with_the_projects_tool()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var project = (await admin.JsonAsync(await admin.PostAsync("/api/projects", new { name = "Launch " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        var chat = (await admin.JsonAsync(await admin.PostAsync("/api/chat/conversations", new { useCodeIntel = false }))).GetProperty("id").GetGuid();
        await admin.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{chat}", UriKind.Relative), new { tools = new[] { "plans" } });
        var made = await CallAsync(admin, chat, """plan it [call plan_make {"title": "Launch", "goal": "Live by December", "steps": [{"title": "Design", "owner": "Ann", "start": "2026-11-01", "due": "2026-11-10"}, {"title": "Build", "owner": "Ben", "due": "2026-11-25", "depends_on": [1]}]}]""");
        var plan = JsonDocument.Parse(made.GetProperty("text").GetString()!).RootElement.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri($"/api/plans/{plan}/steps/1", UriKind.Relative), new { status = "done" }));

        // Put in the project: its plan is the project's goal of the same id, its steps the goal's tasks.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{chat}/project", UriKind.Relative), new { projectId = project }));
        var moved = (await admin.JsonAsync(await admin.GetAsync($"/api/plans/{plan}"))).GetProperty("moved");
        Assert.Equal((project, "Launch"), (moved.GetProperty("projectId").GetGuid(), moved.GetProperty("goal").GetString()));
        var p = await admin.JsonAsync(await admin.GetAsync($"/api/projects/{project}"));
        var goal = p.GetProperty("goals").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == plan);
        Assert.Equal(("Live by December", "doing", "2026-11-25"), (goal.GetProperty("description").GetString(), goal.GetProperty("status").GetString(), goal.GetProperty("dueOn").GetString()));
        Assert.Equal(chat, goal.GetProperty("from").GetProperty("id").GetGuid());
        var tasks = p.GetProperty("tasks").EnumerateArray().Where(t => t.GetProperty("goalId").ValueKind == JsonValueKind.String && t.GetProperty("goalId").GetGuid() == plan)
            .ToDictionary(t => t.GetProperty("title").GetString()!);
        Assert.Equal(("done", true, "Ann", "2026-11-01"), (tasks["Design"].GetProperty("status").GetString(), tasks["Design"].GetProperty("done").GetBoolean(), tasks["Design"].GetProperty("owner").GetString(), tasks["Design"].GetProperty("startOn").GetString()));
        Assert.Equal(("todo", "Ben", "2026-11-25"), (tasks["Build"].GetProperty("status").GetString(), tasks["Build"].GetProperty("owner").GetString(), tasks["Build"].GetProperty("dueOn").GetString()));
        Assert.Equal([tasks["Design"].GetProperty("id").GetGuid()], tasks["Build"].GetProperty("dependsOn").EnumerateArray().Select(x => x.GetGuid()));

        // There the chat plans with the project's tool, not a plan of its own: a task blocked, given to a team, due, waiting.
        var marker = "in-project-" + Guid.NewGuid().ToString("N")[..8];
        var build = tasks["Build"].GetProperty("id").GetGuid();
        var changed = await CallAsync(admin, chat, $$"""{{marker}} [call project_task {"id": "{{build}}", "status": "blocked", "owner": "Platform team", "notes": "Waits for the cluster.", "waits_for": ["{{tasks["Design"].GetProperty("id").GetGuid()}}"]}]""");
        Assert.False(changed.GetProperty("isError").GetBoolean(), changed.GetProperty("text").GetString());
        Assert.Contains("blocked; Platform team; due 2026-11-25; waits for \"Design\"", changed.GetProperty("text").GetString(), StringComparison.Ordinal);
        var offered = app.Model.Requests.Last(r => r.Body["messages"]!.ToJsonString().Contains(marker, StringComparison.Ordinal)).Body["tools"]!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("project_task", offered);
        Assert.DoesNotContain("plan_make", offered);
        var after = (await admin.JsonAsync(await admin.GetAsync($"/api/projects/{project}"))).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == build);
        Assert.Equal(("blocked", false, "Waits for the cluster."), (after.GetProperty("status").GetString(), after.GetProperty("done").GetBoolean(), after.GetProperty("notes").GetString()));

        // Ticked done on the page, its status is done; taken back, to do.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/projects/{project}/tasks/{build}", UriKind.Relative), new { done = true }));
        Assert.Equal("done", (await admin.JsonAsync(await admin.GetAsync($"/api/projects/{project}"))).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == build).GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/projects/{project}/tasks/{build}", UriKind.Relative), new { done = false }));
        var back = (await admin.JsonAsync(await admin.GetAsync($"/api/projects/{project}"))).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == build);
        Assert.Equal(("todo", false), (back.GetProperty("status").GetString(), back.GetProperty("done").GetBoolean()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/projects/{project}/tasks/{build}", UriKind.Relative), new { status = "someday" }));
    }
}
