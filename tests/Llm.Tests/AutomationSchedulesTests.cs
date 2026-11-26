using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Studio;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// What scheduled tasks did, as Automations' workflows: a schedule no more often than allowed and as many as a person may
/// have, run as its owner into a chat (one carried on, if asked), told under the bell, by email and to a webhook whose
/// address stays a secret; and an event posted to a scheduled task's old address runs the workflow of its id.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AutomationSchedulesTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeSmtp? smtp = null, Dictionary<string, string?>? more = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Auth:DataKey"] = "a-data-key-for-schedule-tests",
            ["Schedules:WebhookHosts"] = "hooks.example.test",
        };
        if (smtp is not null)
        {
            config["Mail:Host"] = "127.0.0.1";
            config["Mail:Port"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            config["Mail:StartTls"] = "false";
            config["Mail:From"] = "LLM Service <llm@example.test>";
        }
        foreach (var (k, v) in more ?? [])
        {
            config[k] = v;
        }
        return app.Create(app.ConnectionStringFor("sched_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), config);
    }

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    /// <summary>An email's text, its transfer encoding undone.</summary>
    private static string BodyOf(string data)
    {
        var split = data.IndexOf("\n\n", StringComparison.Ordinal);
        var (headers, body) = (data[..split], data[(split + 2)..]);
        return headers.Contains("base64", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(body.Split('\n', StringSplitOptions.TrimEntries))))
            : body;
    }

    private static object Digest(string cron = "0 9 * * 1-5", string zone = "Europe/Berlin", string hook = "https://hooks.example.test/T000/B000/secret") => new
    {
        trigger = new { kind = "schedule", cron, timeZone = zone },
        steps = new object[]
        {
            new { id = "answer", kind = "agent", name = "Answer", prompt = "Summarize what changed yesterday." },
            new { id = "tell", kind = "notify", subject = "Morning digest", text = "{{answer.text}}" },
            new { id = "mail", kind = "email", subject = "Morning digest", text = "{{answer.text}}" },
            new { id = "post", kind = "webhook", url = hook, text = "Morning digest\n\n{{answer.text}}" },
        },
    };

    /// <summary>A workflow of one agent step made and run by hand, as nobody watches it: the run as it ended, and its chat's id.</summary>
    internal static async Task<(JsonElement Run, Guid Chat)> RunAgentAsync(TestBrowser b, WebApplicationFactory<Program> f, string prompt, string[]? tools = null, Guid? workflow = null)
    {
        var id = workflow ?? (await b.JsonAsync(await b.PostAsync("/api/studio", new
        {
            name = "Agent " + Guid.NewGuid().ToString("N")[..6],
            flow = new { trigger = new { kind = "manual" }, steps = new object[] { new { id = "answer", kind = "agent", prompt, tools } } },
        }))).GetProperty("workflow").GetProperty("id").GetGuid();
        var started = await b.JsonAsync(await b.PostAsync($"/api/studio/{id}/run", new { text = "" }));
        var run = await EndedAsync(b, f, started.GetProperty("id").GetGuid());
        return (run, Guid.Parse(run.GetProperty("steps")[0].GetProperty("link").GetString()!["/chat/".Length..]));
    }

    internal static async Task<JsonElement> EndedAsync(TestBrowser b, WebApplicationFactory<Program> f, Guid run)
    {
        await f.Services.GetRequiredService<StudioRuns>().Finished(run).WaitAsync(TimeSpan.FromSeconds(30));
        return await b.JsonAsync(await b.GetAsync($"/api/studio/runs/{run}"));
    }

    [Fact]
    public async Task A_scheduled_workflow_runs_as_its_owner_into_a_chat_told_by_bell_email_and_a_webhook_whose_address_stays_secret()
    {
        await using var smtp = new FakeSmtp();
        await using var f = NewApp(smtp);
        var (b, email) = await PersonAsync(f);

        // Refused: too often, a host not allowed, not https, no such zone, no schedule.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "Digest", flow = Digest(cron: "*/5 * * * *") }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "Digest", flow = Digest(hook: "https://intranet.example.test/hook") }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "Digest", flow = Digest(hook: "http://hooks.example.test/T000") }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "Digest", flow = Digest(zone: "Mars/Olympus_Mons") }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/studio", new { name = "Digest", flow = Digest(cron: "0 9 * *") }));

        var made = await b.PostAsync("/api/studio", new { name = "Morning digest", flow = Digest() });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var workflow = (await b.JsonAsync(made)).GetProperty("workflow");
        var id = workflow.GetProperty("id").GetGuid();
        // The webhook's address is a secret: shown only as set, never sent back.
        Assert.Equal(FlowModel.Sealed, workflow.GetProperty("flow").GetProperty("steps")[3].GetProperty("secret").GetString());
        Assert.DoesNotContain("B000", await (await b.GetAsync($"/api/studio/{id}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(workflow.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        // Run now: a chat with the question and its answer, the bell, an email, a post.
        var started = await b.JsonAsync(await b.PostAsync($"/api/studio/{id}/run", new { text = "" }));
        var run = await EndedAsync(b, f, started.GetProperty("id").GetGuid());
        Assert.Equal("done", run.GetProperty("status").GetString());
        var chatLink = run.GetProperty("steps")[0].GetProperty("link").GetString()!;
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chatLink["/chat/".Length..]}"));
        Assert.Equal("Morning digest · Answer", chat.GetProperty("title").GetString());
        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("Summarize what changed yesterday.", messages[0].GetProperty("content").GetString());
        Assert.Equal("Answer to: Summarize what chang", messages[^1].GetProperty("content").GetString());

        var notes = await b.JsonAsync(await b.GetAsync("/api/notifications"));
        Assert.Equal(1, notes.GetProperty("unread").GetInt32());
        Assert.Equal("Morning digest", notes.GetProperty("items")[0].GetProperty("title").GetString());

        var mail = Assert.Single(smtp.Received);
        Assert.Contains(email, mail.To[0], StringComparison.Ordinal);
        Assert.Contains("Answer to: Summarize what chang", BodyOf(mail.Data), StringComparison.Ordinal);

        var post = Assert.Single(app.Webhook.Posts, p => p.Url.AbsolutePath == "/T000/B000/secret");
        var posted = JsonDocument.Parse(post.Body).RootElement;
        Assert.Contains("Answer to: Summarize what chang", posted.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Morning digest", posted.GetProperty("workflow").GetString());

        // Saved again as it was shown, the sealed address stays: the next run posts to it too.
        var shown = (await b.JsonAsync(await b.GetAsync($"/api/studio/{id}"))).GetProperty("flow");
        await StatusAssert.Is(HttpStatusCode.OK, await b.Http.PutAsJsonAsync(new Uri($"/api/studio/{id}", UriKind.Relative), new { name = "Morning digest", flow = shown }));
        started = await b.JsonAsync(await b.PostAsync($"/api/studio/{id}/run", new { text = "" }));
        Assert.Equal("done", (await EndedAsync(b, f, started.GetProperty("id").GetGuid())).GetProperty("status").GetString());
        Assert.Equal(2, app.Webhook.Posts.Count(p => p.Url.AbsolutePath == "/T000/B000/secret"));

        // Someone else neither sees it nor runs it.
        var (other, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/studio/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.PostAsync($"/api/studio/{id}/run", new { text = "" }));
    }

    [Fact]
    public async Task Due_by_the_clock_it_runs_once_carries_on_one_chat_and_a_person_has_as_many_schedules_as_allowed()
    {
        await using var f = NewApp(more: new() { ["Schedules:PerPerson"] = "1" });
        var (b, _) = await PersonAsync(f);
        var flow = new
        {
            trigger = new { kind = "schedule", cron = "0 8 * * MON", timeZone = "UTC" },
            steps = new object[] { new { id = "answer", kind = "agent", prompt = "What is new this week?", sameChat = true } },
        };
        var id = (await b.JsonAsync(await b.PostAsync("/api/studio", new { name = "Weekly", flow }))).GetProperty("workflow").GetProperty("id").GetGuid();
        // One schedule is as many as this person may have; one by hand is not a schedule.
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync("/api/studio", new { name = "Second", flow }));
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/studio", new { name = "By hand", flow = flow with { trigger = new { kind = "manual", cron = "", timeZone = "UTC" } } }));

        var runs = f.Services.GetRequiredService<StudioRuns>();
        async Task<Guid> DueNowAsync()
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Workflows.Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.NextRunAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, await runs.DueAsync(CancellationToken.None));
            Assert.Equal(0, await runs.DueAsync(CancellationToken.None));
            var run = await db.WorkflowRuns.AsNoTracking().Where(r => r.WorkflowId == id).OrderByDescending(r => r.StartedAt).FirstAsync();
            Assert.Equal("schedule", run.Trigger);
            return run.Id;
        }

        var first = await EndedAsync(b, f, await DueNowAsync());
        var next = (await b.JsonAsync(await b.GetAsync($"/api/studio/{id}"))).GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.True(next > DateTimeOffset.UtcNow);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);

        // The second run carries on the same chat: it reads the first.
        var second = await EndedAsync(b, f, await DueNowAsync());
        var link = first.GetProperty("steps")[0].GetProperty("link").GetString();
        Assert.Equal(link, second.GetProperty("steps")[0].GetProperty("link").GetString());
        var chat = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{link!["/chat/".Length..]}"));
        Assert.Equal("Weekly", chat.GetProperty("title").GetString());
        Assert.Equal(2, chat.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("role").GetString() == "user"));

        // Removed: its chats stay.
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.DeleteAsync(new Uri($"/api/studio/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync($"/api/chat/conversations/{link["/chat/".Length..]}"));
    }

    [Fact]
    public async Task Schedules_switched_off_for_everyone_neither_run_nor_can_be_set_and_an_event_at_a_tasks_old_address_runs_its_workflow()
    {
        await using var f = NewApp(more: new() { ["Schedules:Enabled"] = "false" });
        var (b, _) = await PersonAsync(f);
        var scheduled = new { trigger = new { kind = "schedule", cron = "0 9 * * *", timeZone = "UTC" }, steps = new object[] { new { id = "say", kind = "text", text = "hi" } } };
        var off = await b.PostAsync("/api/studio", new { name = "Daily", flow = scheduled });
        await StatusAssert.Is(HttpStatusCode.Forbidden, off);
        Assert.Contains("Settings → Automations", await off.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A webhook workflow: what a system posts to /api/hooks/{id} (a scheduled task's address) with its secret runs it.
        var hooked = await b.JsonAsync(await b.PostAsync("/api/studio", new
        {
            name = "Alert",
            flow = new { trigger = new { kind = "webhook" }, steps = new object[] { new { id = "say", kind = "text", text = "Alert: {{input.text}}" } } },
        }));
        var id = hooked.GetProperty("workflow").GetProperty("id").GetGuid();
        var secret = hooked.GetProperty("secret").GetString()!;
        var anyone = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        using var wrong = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/{id}") { Content = JsonContent.Create(new { text = "disk full" }) };
        wrong.Headers.Add("X-Hook-Secret", "not-it");
        Assert.False((await anyone.SendAsync(wrong)).IsSuccessStatusCode);
        using var right = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/{id}") { Content = JsonContent.Create(new { text = "disk full" }) };
        right.Headers.Add("X-Hook-Secret", secret);
        await StatusAssert.Is(HttpStatusCode.Accepted, await anyone.SendAsync(right));
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.WorkflowId == id);
        var ended = await EndedAsync(b, f, run.Id);
        Assert.Equal(("webhook", "done"), (run.Trigger, ended.GetProperty("status").GetString()));
        Assert.Contains("disk full", ended.GetProperty("steps")[0].GetProperty("output").GetString(), StringComparison.Ordinal);
    }
}
