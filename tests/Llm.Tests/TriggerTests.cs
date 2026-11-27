// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

using Branding;
namespace Llm.Tests;

/// <summary>Workflows that events run: GitLab's (a merge request with its changes, a failed pipeline with its job's log), with the answer commented back; and any system's JSON.</summary>
[Collection(nameof(AppCollection))]
public sealed class TriggerTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("triggers_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-trigger-tests", ["GitLab:Url"] = "https://gitlab.test", ["GitLab:BotToken"] = "bot-token",
        });

    private static async Task<TestBrowser> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "g" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static Task<HttpResponseMessage> PostEvent(WebApplicationFactory<Program> f, string url, string? secret, object body, string header = "X-Gitlab-Token")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url).PathAndQuery) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (secret is not null)
        {
            request.Headers.Add(header, secret);
        }
        return f.CreateClient().SendAsync(request);
    }

    /// <summary>The run an event started, as it ended.</summary>
    private static async Task<JsonElement> RunAsync(TestBrowser b, WebApplicationFactory<Program> f, HttpResponseMessage started)
    {
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("run").GetGuid();
        return await AutomationSchedulesTests.EndedAsync(b, f, run);
    }

    private static object Agent(string kind, string prompt, string[]? events = null, bool reply = false) => new
    {
        trigger = new { kind, events },
        steps = reply
            ? new object[] { new { id = "answer", kind = "agent", prompt = prompt + "\n\n{{input.text}}", tools = Array.Empty<string>() }, new { id = "reply", kind = "comment", text = "{{answer.text}}" } }
            : [new { id = "answer", kind = "agent", prompt = prompt + "\n\n{{input.text}}", tools = Array.Empty<string>() }],
    };

    private static object MergeRequest(string action) => new
    {
        object_kind = "merge_request", user = new { name = "Dana" }, project = new { id = 7, path_with_namespace = "group/app" },
        object_attributes = new { iid = 3, action, title = "Widen DecodeFrame", source_branch = "wide", target_branch = "main", url = "https://gitlab.test/group/app/-/merge_requests/3", description = "Takes long now." },
    };

    [Fact]
    public async Task A_merge_request_runs_a_review_with_its_changes_and_the_answer_is_commented_back()
    {
        await using var f = NewApp();
        var b = await PersonAsync(f);
        var made = await b.JsonAsync(await b.PostAsync("/api/studio", new { name = "Review", flow = Agent("gitlab", "Review this merge request.", ["merge_request"], reply: true) }));
        var workflow = made.GetProperty("workflow");
        var url = workflow.GetProperty("hook").GetString()!;
        var secret = made.GetProperty("secret").GetString()!;
        Assert.Equal($"https://llm.test/api/studio/hooks/{workflow.GetProperty("id").GetGuid()}", url);
        Assert.Equal(JsonValueKind.Null, workflow.GetProperty("nextRunAt").ValueKind);
        // The secret is shown once.
        Assert.DoesNotContain(secret, await (await b.GetAsync($"/api/studio/{workflow.GetProperty("id").GetGuid()}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(f, url, "wrong", MergeRequest("open"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(f, url, null, MergeRequest("open"))).StatusCode);
        var closed = await PostEvent(f, url, secret, MergeRequest("close"));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Contains("ignored", await closed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var run = await RunAsync(b, f, await PostEvent(f, url, secret, MergeRequest("open")));
        Assert.Equal("done", run.GetProperty("status").GetString());
        // The question: the workflow's, then the event with the changes the bot read.
        var question = app.Model.Requests.Select(r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString())
            .Last(c => c.StartsWith("Review this merge request.", StringComparison.Ordinal));
        Assert.Contains("merge request !3 was opened by Dana in group/app", question, StringComparison.Ordinal);
        Assert.Contains("+int DecodeFrame(long x);", question, StringComparison.Ordinal);
        var comment = app.Mcp.GitLabCalls.Last(c => c.Method == "POST" && c.PathAndQuery == "/api/v4/projects/7/merge_requests/3/notes");
        Assert.Contains("Answer to: Review this merge", comment.Body, StringComparison.Ordinal);
        Assert.Contains($"by {Brand.Product}", comment.Body, StringComparison.Ordinal);

        // A new secret: the old one stops working.
        var rotated = (await b.JsonAsync(await b.PostAsync($"/api/studio/{workflow.GetProperty("id").GetGuid()}/secret", new { }))).GetProperty("secret").GetString();
        Assert.NotEqual(secret, rotated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(f, url, secret, MergeRequest("open"))).StatusCode);
    }

    [Fact]
    public async Task A_failed_pipeline_brings_its_jobs_log_and_any_system_can_post_json()
    {
        await using var f = NewApp();
        var b = await PersonAsync(f);
        var pipeline = await b.JsonAsync(await b.PostAsync("/api/studio", new { name = "Explain", flow = Agent("gitlab", "Explain why it failed.", ["pipeline_failed"]) }));
        var (url, secret) = (pipeline.GetProperty("workflow").GetProperty("hook").GetString()!, pipeline.GetProperty("secret").GetString());
        var ok = await PostEvent(f, url, secret, new
        {
            object_kind = "pipeline", project = new { id = 7, path_with_namespace = "group/app" },
            object_attributes = new { id = 12, status = "success", @ref = "main", sha = "abc123def" },
        });
        Assert.Contains("ignored", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var failed = await RunAsync(b, f, await PostEvent(f, url, secret, new
        {
            object_kind = "pipeline", project = new { id = 7, path_with_namespace = "group/app" },
            object_attributes = new { id = 12, status = "failed", @ref = "main", sha = "abc123def" },
            builds = new[] { new { id = 41, name = "build", stage = "build", status = "failed" } },
        }));
        Assert.Equal("done", failed.GetProperty("status").GetString());
        var asked = app.Model.Requests.Select(r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString()).Last(c => c.StartsWith("Explain why", StringComparison.Ordinal));
        Assert.Contains("pipeline #12 failed on main", asked, StringComparison.Ordinal);
        Assert.Contains("undefined reference to DecodeFrame", asked, StringComparison.Ordinal);

        var hook = await b.JsonAsync(await b.PostAsync("/api/studio", new { name = "Alert", flow = Agent("webhook", "Say what this alert means.") }));
        var alert = await RunAsync(b, f, await PostEvent(f, hook.GetProperty("workflow").GetProperty("hook").GetString()!, hook.GetProperty("secret").GetString(), new { alert = "DiskFull", host = "db1" }, "X-Hook-Secret"));
        Assert.Equal("done", alert.GetProperty("status").GetString());
        Assert.Contains(app.Model.Requests, r => r.Body["messages"]!.AsArray().Last()!["content"]!.ToString().Contains("\"alert\":\"DiskFull\"", StringComparison.Ordinal));

        // Events need choosing; a trigger is one there is.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "X", flow = Agent("gitlab", "Y", []) }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "X", flow = Agent("sometimes", "Y") }));
    }
}
