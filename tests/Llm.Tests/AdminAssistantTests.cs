// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// The admin assistant acts as the person signed in and no more: it reads Arena's API through the app's own pipeline as
/// them (a route they may not read is refused as it would be for them), a change is only proposed until their yes (and
/// is then made as them, checked again), nobody else may say yes to it, and the audit log says it came through the assistant.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AdminAssistantTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<(Guid Id, TestBrowser Browser)> PersonAsync(AppFixture app, TestBrowser admin, params string[] permissions)
    {
        var name = "aa" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var id = made.GetProperty("id").GetGuid();
        if (permissions.Length > 0)
        {
            var role = await admin.PostAsync("/api/admin/access/roles", new { name = "Assistant " + Guid.NewGuid().ToString("N")[..8], permissions });
            await StatusAssert.Is(HttpStatusCode.Created, role);
            await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments",
                new { roleId = (await admin.JsonAsync(role)).GetProperty("id").GetGuid(), subjectKind = "Person", subjectId = id }));
        }
        return (id, await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!));
    }

    private static string Script(params object[][] rounds) => "[script " + JsonSerializer.Serialize(rounds) + "]";

    private static async Task<List<JsonElement>> AskAsync(TestBrowser b, string question, string page = "/admin/people")
    {
        var res = await b.PostAsync("/api/admin-assistant/ask", new { messages = new[] { new { role = "user", content = question } }, page });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    /// <summary>What the model was given back for its tool calls on the question with this marker.</summary>
    private string ToolResults(string marker) => string.Join("\n", app.Model.Requests.Select(r => r.Body)
        .Where(b => b["messages"]!.AsArray().Any(m => m?["role"]?.ToString() == "user" && m["content"]!.ToString().Contains(marker, StringComparison.Ordinal)))
        .SelectMany(b => b["messages"]!.AsArray().Where(m => m?["role"]?.ToString() == "tool").Select(m => m!["content"]!.ToString())));

    [Fact]
    public async Task Its_thinking_comes_as_it_thinks_then_how_long_it_thought_then_its_words()
    {
        var admin = await AdminAsync();
        var events = await AskAsync(admin, "[ponder] how do I add a group?");
        var types = events.Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Contains("thinking", types);
        var thought = types.IndexOf("thought");
        Assert.True(thought > types.IndexOf("thinking") && thought < types.IndexOf("delta"), string.Join(",", types));
        Assert.True(events[thought].GetProperty("ms").GetInt32() >= 0);
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task It_reads_as_the_person_proposes_a_change_and_makes_it_only_on_their_yes_audited_through_the_assistant()
    {
        var admin = await AdminAsync();
        var marker = "assist-" + Guid.NewGuid().ToString("N")[..8];
        var name = "new" + Guid.NewGuid().ToString("N")[..10];
        var events = await AskAsync(admin, $"{marker} add {name} " + Script(
            [new { name = "find_api", arguments = new { words = "people" } }, new { name = "read", arguments = new { path = "/api/admin/people" } }],
            [new { name = "propose_change", arguments = new { method = "POST", path = "/api/admin/people", body = new { userName = name, email = $"{name}@example.test" }, what = $"Adds {name}." } }]));

        // It read the people as the admin, found the route, and proposed the change.
        var results = ToolResults(marker);
        Assert.Contains("POST /api/admin/people [people.create] body {userName: text", results, StringComparison.Ordinal);
        Assert.Contains("HTTP 200", results, StringComparison.Ordinal);
        Assert.Contains("\"admin\"", results, StringComparison.Ordinal);
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "step" && e.TryGetProperty("status", out var s) && s.GetInt32() == 200);
        // Each step shows as the chat's tool calls do: running, then done with what came back, by the same id.
        var steps = events.Where(e => e.GetProperty("type").GetString() == "step").ToList();
        foreach (var running in steps.Where(e => e.GetProperty("state").GetString() == "running"))
        {
            Assert.Contains(steps, e => e.GetProperty("id").GetString() == running.GetProperty("id").GetString() && e.GetProperty("state").GetString() is "done" or "failed"
                && !string.IsNullOrEmpty(e.GetProperty("result").GetString()));
        }
        Assert.Equal(2, steps.Count(e => e.GetProperty("state").GetString() == "running"));
        // Its words come as they are written, a round at a time.
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "delta");
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "round");
        var proposal = events.Single(e => e.GetProperty("type").GetString() == "proposal");
        Assert.Equal($"Adds {name}.", proposal.GetProperty("what").GetString());
        Assert.Equal("done", events[^1].GetProperty("type").GetString());

        // Nothing is made until the yes; nobody else may say it.
        var people = await admin.JsonAsync(await admin.GetAsync($"/api/admin/people?q={name}"));
        Assert.DoesNotContain(name, people.ToString(), StringComparison.Ordinal);
        var id = proposal.GetProperty("id").GetGuid();
        var (_, other) = await PersonAsync(app, admin, "people.read", "people.manage");
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/admin-assistant/changes/{id}/yes", new { }));

        var yes = await admin.JsonAsync(await admin.PostAsync($"/api/admin-assistant/changes/{id}/yes", new { }));
        Assert.Equal(201, yes.GetProperty("status").GetInt32());
        Assert.Contains(name, (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people?q={name}"))).ToString(), StringComparison.Ordinal);
        // Once only.
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync($"/api/admin-assistant/changes/{id}/yes", new { }));

        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?action=assistant.change.made"))).EnumerateArray().First();
        Assert.Equal("POST /api/admin/people", audit.GetProperty("target").GetString());
        Assert.Contains($"Adds {name}.; HTTP 201", audit.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("admin", audit.GetProperty("actor").GetString());
        // The change itself, as the person, says it came through the assistant.
        var made = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/audit?q={name}"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() != "assistant.change.made").ToList();
        Assert.Contains(made, e => e.GetProperty("detail").GetString()?.Contains("through the assistant", StringComparison.Ordinal) == true && e.GetProperty("actor").GetString() == "admin");
    }

    [Fact]
    [Trait("Control", "IAM-05")]
    public async Task It_cannot_do_through_the_assistant_what_the_person_may_not_do_and_a_no_leaves_it_undone()
    {
        var admin = await AdminAsync();
        // Without any admin permission: no assistant.
        var (_, member) = await PersonAsync(app, admin);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.PostAsync("/api/admin-assistant/ask", new { messages = new[] { new { role = "user", content = "hello" } } }));

        // Models only: the people are refused to it as to them, and a change it may not make is not proposed.
        var (_, models) = await PersonAsync(app, admin, "models.read");
        var marker = "assist-" + Guid.NewGuid().ToString("N")[..8];
        var events = await AskAsync(models, $"{marker} " + Script(
            [new { name = "read", arguments = new { path = "/api/admin/people" } }],
            [new { name = "propose_change", arguments = new { method = "POST", path = "/api/admin/people", body = new { userName = "x", email = "x@example.test" }, what = "Adds x." } }],
            [new { name = "read", arguments = new { path = "/api/admin-assistant/ask" } }]));
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "step" && e.TryGetProperty("status", out var s) && s.GetInt32() == 403);
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "proposal");
        var results = ToolResults(marker);
        Assert.Contains("HTTP 403", results, StringComparison.Ordinal);
        Assert.Contains("may not: it needs people.create", results, StringComparison.Ordinal);
        Assert.Contains("Not the assistant's own routes.", results, StringComparison.Ordinal);

        // A no: nothing done, and audited.
        var name = "no" + Guid.NewGuid().ToString("N")[..10];
        var marker2 = "assist-" + Guid.NewGuid().ToString("N")[..8];
        var proposed = (await AskAsync(admin, $"{marker2} " + Script(
            [new { name = "propose_change", arguments = new { method = "POST", path = "/api/admin/people", body = new { userName = name, email = $"{name}@example.test" }, what = $"Adds {name}." } }])))
            .Single(e => e.GetProperty("type").GetString() == "proposal").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin-assistant/changes/{proposed}/no", new { }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync($"/api/admin-assistant/changes/{proposed}/yes", new { }));
        Assert.DoesNotContain(name, (await admin.JsonAsync(await admin.GetAsync($"/api/admin/people?q={name}"))).ToString(), StringComparison.Ordinal);
        Assert.Contains((await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?action=assistant.change.declined"))).EnumerateArray(),
            e => e.GetProperty("detail").GetString() == $"Adds {name}.");
    }

    [Fact]
    public void A_bodys_shape_names_its_fields_as_JSON_does()
    {
        var shape = Llm.Api.AdminHelp.AdminAssistant.Shape(typeof(Sample), 0);
        Assert.Equal("{name: text, on: true|false, count: number, ids: [id], when: time, inner: {size: number}}", shape);
    }

    private sealed record Inner(int Size);

    private sealed record Sample(string Name, bool On, int? Count, Guid[] Ids, DateTimeOffset When, Inner Inner);
}
