using System.Net;
using System.Text;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Admin → Prompt log: everyone's questions and answers, the chat's and the API's, for prompts.review only; every search audited.</summary>
[Collection(nameof(AppCollection))]
public sealed class PromptLogTests(AppFixture app)
{
    private static async Task<HttpResponseMessage> AskAsync(WebApplicationFactory<Program> f, string key, string text)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "Qwen3.8-Flash-Next", messages = new object[] { new { role = "system", content = "Be brief." }, new { role = "user", content = text } } }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        var res = await client.SendAsync(req);
        await res.Content.ReadAsStringAsync();
        return res;
    }

    /// <summary>A step of an agent run through a key: its messages, the tools it offers, and its run's chat tag.</summary>
    private static async Task<JsonElement> StepAsync(WebApplicationFactory<Program> f, string key, Guid chat, object[] messages)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "Qwen3.8-Flash-Next", messages, stream = true, temperature = 0.2,
                tools = new[] { new { type = "function", function = new { name = "read_file", parameters = new { type = "object" } } } },
                metadata = new { tags = new[] { "chat:" + chat } },
            }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        using var res = await client.SendAsync(req);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        await res.Content.ReadAsStringAsync();
        return default;
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task A_report_is_written_on_the_filtered_log_with_the_model_chosen_for_reviewers_only_and_audited()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptrep_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "rp" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var other = "ro" + Guid.NewGuid().ToString("N")[..8];
        var otherMade = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = other, email = $"{other}@example.test" }));
        await AskAsync(f, made.GetProperty("apiKey").GetString()!, "How do I sort a list in Python?");
        await AskAsync(f, made.GetProperty("apiKey").GetString()!, "fix it");
        await AskAsync(f, otherMade.GetProperty("apiKey").GetString()!, "What is the capital of France?");
        await f.Services.GetRequiredService<PromptLogWriter>().FlushAsync();
        for (var waited = 0; waited < 10_000 && (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}"))).GetProperty("items").GetArrayLength() < 2; waited += 50)
        {
            await Task.Delay(50);
        }

        // On one person's lines only: the report's events end with the report, which counts their two lines.
        var res = await admin.PostAsync("/api/admin/prompts/write", new { ask = "How good are these prompts?", person = name, model = "Qwen3.8-Flash-Next" });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var events = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l[6..]).RootElement).ToList();
        var report = events.Last();
        Assert.Equal("report", report.GetProperty("type").GetString());
        Assert.Equal(2, report.GetProperty("lines").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("markdown").GetString()));
        // The model was given only those lines: theirs, not the other person's.
        var asked = string.Join("\n", app.Model.Requests.Select(r => r.Body.ToJsonString()).Where(b => b.Contains("How good are these prompts", StringComparison.Ordinal)));
        Assert.Contains("sort a list in Python", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("capital of France", asked, StringComparison.Ordinal);
        // Audited: who, the filters, the model; and not for someone without prompts.review.
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?action=prompts.report.written"))).EnumerateArray().Single();
        Assert.Contains($"person {name}", audit.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("2 lines", audit.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.PostAsync("/api/admin/prompts/write", new { ask = "Everything" }));
    }

    [Fact]
    public void A_long_log_is_read_a_part_at_a_time_no_line_split()
    {
        var lines = Enumerable.Range(0, 100).Select(i => new string('x', 1000) + i).ToList();
        var parts = Llm.Api.Quality.PromptLogReports.Parts(lines);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= Llm.Api.Quality.PromptLogReports.PartChars + 1100));
        Assert.Equal(lines, parts.SelectMany(p => p.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task A_run_is_shown_whole_each_step_with_its_new_messages_thinking_words_and_tool_calls_by_thread_for_reviewers_only()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptrun_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pr" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var chat = Guid.NewGuid();
        var system = new { role = "system", content = "You are a coding agent." };
        var ask = new { role = "user", content = """Fix the build [call read_file {"path":"src/a.cs"}]""" };
        // The main agent's first step calls a tool; its second carries the call and the tool's result; a sub-agent starts its own.
        await StepAsync(f, key, chat, [system, ask]);
        var call = new { role = "assistant", content = "", tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "read_file", arguments = """{"path":"src/a.cs"}""" } } } };
        await StepAsync(f, key, chat, [system, ask, call, new { role = "tool", tool_call_id = "call_1", content = "class A { int x = ; }" }]);
        await StepAsync(f, key, chat, [new { role = "system", content = "You are a sub-agent: look, report." }, new { role = "user", content = "Find where A is used." }]);
        // Each step ends as its answer has gone out: until all three are done, then what they wrote.
        JsonElement run = default;
        List<JsonElement> steps = [];
        for (var waited = 0; waited < 10_000 && (steps.Count < 3 || steps.Any(x => x.GetProperty("state").GetString() != "done")); waited += 50)
        {
            run = await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts/runs/{chat}"));
            steps = [.. run.GetProperty("steps").EnumerateArray()];
            await Task.Delay(50);
        }
        await f.Services.GetRequiredService<PromptLogWriter>().FlushAsync();
        Assert.Equal(3, steps.Count);
        Assert.Equal(["Main", "Sub-agent 1"], run.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(steps[0].GetProperty("thread").GetString(), steps[1].GetProperty("thread").GetString());
        Assert.NotEqual(steps[0].GetProperty("thread").GetString(), steps[2].GetProperty("thread").GetString());
        // Found by its run, by a tool its answer called, by its model; nothing by another model.
        int Count(JsonElement r) => r.GetProperty("items").GetArrayLength();
        Assert.Equal(3, Count(await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?source=api&run={chat}"))));
        Assert.Equal(1, Count(await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?source=api&run={chat}&tool=read_file"))));
        Assert.Equal(3, Count(await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?source=api&run={chat}&model=Qwen3.8-Flash-Next"))));
        Assert.Equal(0, Count(await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?source=api&run={chat}&model=another"))));
        // Each line of the log says which run it is a step of.
        var listed = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}&source=api"))).GetProperty("items").EnumerateArray().ToList();
        Assert.All(listed, i => Assert.Equal(chat, i.GetProperty("run").GetGuid()));

        // The first step whole: its messages, the tools it offered, its settings, and the call it made with its arguments.
        var first = await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts/api/{steps[0].GetProperty("id").GetGuid()}/detail"));
        Assert.Equal((0, 2), (first.GetProperty("from").GetInt32(), first.GetProperty("total").GetInt32()));
        Assert.Equal(["system", "user"], first.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(["read_file"], first.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(0.2, first.GetProperty("settings").GetProperty("temperature").GetDouble());
        var made1 = Assert.Single(first.GetProperty("answer").GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(("read_file", """{"path":"src/a.cs"}"""), (made1.GetProperty("name").GetString(), made1.GetProperty("arguments").GetString()));
        // The second: only what is new (the call and the tool's result), not the whole history again.
        var second = await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts/api/{steps[1].GetProperty("id").GetGuid()}/detail"));
        Assert.Equal((2, 4), (second.GetProperty("from").GetInt32(), second.GetProperty("total").GetInt32()));
        var news = second.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["assistant", "tool"], news.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("class A { int x = ; }", news[1].GetProperty("content").GetString());
        Assert.Equal("read_file", news[0].GetProperty("toolCalls")[0].GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(second.GetProperty("answer").GetProperty("content").GetString()));

        // A chat question's answer round by round: its tool call and the tool's result.
        var c = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { tools = new[] { "calculator" } }))).GetProperty("id").GetGuid();
        await (await person.PostAsync($"/api/chat/conversations/{c}/messages", new { content = """Sum it [call calculate {"expression":"2+3"}]""" })).Content.ReadAsStringAsync();
        var q = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}&source=chat"))).GetProperty("items").EnumerateArray().Single();
        var asked = await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts/chat/{q.GetProperty("id").GetGuid()}/detail"));
        var rounds = asked.GetProperty("steps").EnumerateArray().ToList();
        Assert.Contains(rounds, r => r.GetProperty("role").GetString() == "assistant" && r.GetProperty("toolCalls").ValueKind == JsonValueKind.Array
            && r.GetProperty("toolCalls")[0].GetProperty("name").GetString() == "calculate");
        Assert.Contains(rounds, r => r.GetProperty("role").GetString() == "tool" && r.GetProperty("tool").GetString() == "calculate");

        // Reviewers only, and each look audited.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync($"/api/admin/prompts/runs/{chat}"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync($"/api/admin/prompts/api/{steps[0].GetProperty("id").GetGuid()}/detail"));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("prompts.run.viewed", audit);
        Assert.Contains("prompts.detail.viewed", audit);
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task The_chats_and_the_API_requests_questions_and_answers_are_read_with_prompts_review_and_each_search_is_audited()
    {
        await using var f = app.Create(app.ConnectionStringFor("prompts_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pl" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);

        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        var sent = await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Which port does Postgres use?" });
        await sent.Content.ReadAsStringAsync();
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(f, made.GetProperty("apiKey").GetString()!, "Name a prime number."));

        // The API request is kept after its answer went out.
        List<JsonElement> items = [];
        for (var waited = 0; waited < 10_000; waited += 100)
        {
            items = [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}"))).GetProperty("items").EnumerateArray()];
            if (items.Any(i => i.GetProperty("source").GetString() == "api"))
            {
                break;
            }
            await Task.Delay(100);
        }
        var api = items.Single(i => i.GetProperty("source").GetString() == "api");
        Assert.Equal("Name a prime number.", api.GetProperty("question").GetString());
        Assert.StartsWith("Answer to:", api.GetProperty("answer").GetString(), StringComparison.Ordinal);
        var asked = items.Single(i => i.GetProperty("source").GetString() == "chat");
        Assert.Equal("Which port does Postgres use?", asked.GetProperty("question").GetString());
        Assert.StartsWith("Answer to:", asked.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(chat, asked.GetProperty("chat").GetGuid());
        Assert.Equal(name, asked.GetProperty("person").GetProperty("userName").GetString());

        // Searched by words, and each search audited with what was asked.
        var found = (await admin.JsonAsync(await admin.GetAsync("/api/admin/prompts?q=postgres&source=chat"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Which port does Postgres use?", Assert.Single(found).GetProperty("question").GetString());
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().ToList();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "prompts.viewed" && e.GetProperty("detail").GetString()!.Contains("text \"postgres\"", StringComparison.Ordinal));

        // Not theirs to read, nor an Auditor's, who holds every ".read".
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/prompts"));
        var auditorName = "au" + Guid.NewGuid().ToString("N")[..8];
        var auditor = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = auditorName, email = $"{auditorName}@example.test" }));
        var roles = (await admin.JsonAsync(await admin.GetAsync("/api/admin/access"))).GetProperty("roles").EnumerateArray();
        var role = roles.Single(r => r.GetProperty("key").GetString() == "auditor").GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = auditor.GetProperty("id").GetGuid() }));
        var them = await new TestBrowser(f).SignedInAsync(auditorName, auditor.GetProperty("password").GetString()!);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await them.GetAsync("/api/admin/prompts"));
    }

    /// <summary>A PNG's start: what makes an upload a picture.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];

    [Fact]
    public async Task A_questions_pictures_and_the_files_its_answer_made_are_in_the_log_for_reviewers_only()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptfiles_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pf" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);

        // In the chat: a picture with the question, and a file the answer made (as a tool that writes one keeps it).
        using var form = new MultipartFormDataContent();
        var upload = new ByteArrayContent(Png);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(upload, "file", "diagram.png");
        var picture = (await person.JsonAsync(await person.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form))).GetProperty("id").GetGuid();
        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "What is in this diagram?", attachments = new[] { picture } })).Content.ReadAsStringAsync();
        Guid report;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var answer = await db.ChatMessages.Where(m => m.ConversationId == chat && m.Role == "assistant").OrderBy(m => m.Sequence).FirstAsync();
            var owner = await db.Users.SingleAsync(u => u.UserName == name);
            var file = new Llm.Core.Chat.ChatAttachment { UserId = owner.Id, FileName = "report.csv", ContentType = "text/csv", Size = 8, Text = "a,b\n1,2\n", Kind = "text" };
            db.ChatAttachments.Add(file);
            db.ChatMessages.Add(new Llm.Core.Chat.ChatMessage
            {
                ConversationId = chat, ParentId = answer.Id, Role = "tool", Sequence = answer.Sequence + 1, ToolCallId = "c1", ToolName = "python", Content = "Wrote report.csv",
                AnswerId = answer.AnswerId ?? answer.Id, AttachmentsJson = JsonSerializer.Serialize(new[] { file.Id }),
            });
            await db.SaveChangesAsync();
            report = file.Id;
        }
        // With an API key: a question with a picture as data.
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative)))
        {
            var parts = new object[] { new { type = "text", text = "Describe it." }, new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(Png) } } };
            // A system message first, as text: the other tests read each request's first message as a string, from the one fake model they share.
            req.Content = new StringContent(JsonSerializer.Serialize(new { model = "Qwen3.8-Flash-Next", messages = new object[] { new { role = "system", content = "Answer briefly." }, new { role = "user", content = parts } } }), Encoding.UTF8, "application/json");
            req.Headers.Authorization = new("Bearer", made.GetProperty("apiKey").GetString()!);
            await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        }

        List<JsonElement> items = [];
        for (var waited = 0; waited < 10_000 && !items.Any(i => i.GetProperty("source").GetString() == "api"); waited += 100)
        {
            items = [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}"))).GetProperty("items").EnumerateArray()];
            await Task.Delay(100);
        }
        var asked = items.Single(i => i.GetProperty("source").GetString() == "chat");
        var shown = Assert.Single(asked.GetProperty("files").EnumerateArray());
        Assert.Equal(("diagram.png", "image"), (shown.GetProperty("name").GetString(), shown.GetProperty("kind").GetString()));
        var reportShown = Assert.Single(asked.GetProperty("made").EnumerateArray());
        Assert.Equal("report.csv", reportShown.GetProperty("name").GetString());
        var api = items.Single(i => i.GetProperty("source").GetString() == "api");
        Assert.Equal("Describe it.", api.GetProperty("question").GetString());
        var apiPicture = Assert.Single(api.GetProperty("files").EnumerateArray());
        Assert.Equal(("picture-1.png", "image/png"), (apiPicture.GetProperty("name").GetString(), apiPicture.GetProperty("contentType").GetString()));

        // A reviewer opens each: the pictures as pictures, the file to save; each look is audited.
        var seen = await admin.GetAsync(shown.GetProperty("url").GetString()!);
        Assert.Equal("image/png", seen.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await seen.Content.ReadAsByteArrayAsync());
        Assert.Equal(Png, await (await admin.GetAsync(apiPicture.GetProperty("url").GetString()!)).Content.ReadAsByteArrayAsync());
        var saved = await admin.GetAsync(reportShown.GetProperty("url").GetString()!);
        Assert.Equal("attachment", saved.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("a,b\n1,2\n", await saved.Content.ReadAsStringAsync());
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            Assert.Equal(3, await db.AuditEvents.CountAsync(a => a.Action == "prompts.file.viewed"));
        }
        // Nobody else: not the person (no prompts.review), and no file that is no message's.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync(shown.GetProperty("url").GetString()!));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/prompts/files/chat/{Guid.NewGuid()}"));
        Assert.NotEqual(Guid.Empty, report);
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task An_answer_from_the_answer_cache_is_in_the_prompt_log_as_well()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptscache_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Gateway:AnswerCache"] = "all",
            ["Gateway:KeyCacheSeconds"] = "0",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var question = "Cached " + Guid.NewGuid().ToString("N")[..8];
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(f, key, question));
        var hit = await AskAsync(f, key, question);
        Assert.Equal("hit", hit.Headers.GetValues(AnswerCache.Header).Single());

        List<JsonElement> items = [];
        for (var waited = 0; waited < 10_000 && items.Count < 2; waited += 100)
        {
            items = [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}&source=api"))).GetProperty("items").EnumerateArray()];
            await Task.Delay(100);
        }
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(question, i.GetProperty("question").GetString()));
        Assert.Equal(items[0].GetProperty("answer").GetString(), items[1].GetProperty("answer").GetString());
    }

    private static async Task<List<JsonElement>> LogAsync(TestBrowser admin, string query) =>
        [.. (await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?{query}"))).GetProperty("items").EnumerateArray()];

    /// <summary>The log's lines for a query once <paramref name="ready"/> holds of them (the writes run behind the requests).</summary>
    private static async Task<List<JsonElement>> LogWhenAsync(TestBrowser admin, string query, Func<List<JsonElement>, bool> ready)
    {
        for (var waited = 0; ; waited += 50)
        {
            var items = await LogAsync(admin, query);
            if (ready(items) || waited > 10_000)
            {
                return items;
            }
            await Task.Delay(50);
        }
    }

    private static JsonElement Asked(List<JsonElement> items, string question) => items.Single(i => i.GetProperty("question").GetString() == question);

    private static string StateOf(List<JsonElement> items, string question) =>
        items.FirstOrDefault(i => i.GetProperty("question").GetString() == question) is { ValueKind: JsonValueKind.Object } i ? i.GetProperty("state").GetString()! : "";

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task Every_API_request_is_in_the_log_from_its_arrival_while_it_runs_and_when_it_fails_is_refused_or_is_stopped()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptstates_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Chat:ApiRequestsPerKey"] = "1",
            ["Chat:ApiQueueTimeout"] = "00:00:00.300",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "ps" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;

        // Failed: with the error the caller got from the gateway.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await AskAsync(f, key, "[budget] Spend more."));

        // Running while its answer is written, and the key's next request refused once it waited past its limit.
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var leave = new CancellationTokenSource();
        using var slow = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = "Qwen3.8-Flash-Next", stream = true, messages = new object[] { new { role = "user", content = "[slow] A long story." } } }), Encoding.UTF8, "application/json"),
        };
        slow.Headers.Authorization = new("Bearer", key);
        using var streaming = await client.SendAsync(slow, HttpCompletionOption.ResponseHeadersRead, leave.Token);
        await using var body = await streaming.Content.ReadAsStreamAsync(leave.Token);
        Assert.True(await body.ReadAsync(new byte[64], leave.Token) > 0);
        var items = await LogWhenAsync(admin, $"person={name}", i => StateOf(i, "[slow] A long story.") == "running");
        Assert.Equal("running", StateOf(items, "[slow] A long story."));
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, await AskAsync(f, key, "Another one, please."));

        // Stopped: the caller went away mid-answer.
        await leave.CancelAsync();
        streaming.Dispose();
        items = await LogWhenAsync(admin, $"person={name}", i => StateOf(i, "[slow] A long story.") == "stopped" && StateOf(i, "Another one, please.") == "refused"
            && StateOf(i, "[budget] Spend more.") == "failed");
        Assert.Equal("stopped", StateOf(items, "[slow] A long story."));
        // What had gone out is kept.
        Assert.StartsWith("w0 ", Asked(items, "[slow] A long story.").GetProperty("answer").GetString(), StringComparison.Ordinal);
        var refused = Asked(items, "Another one, please.");
        Assert.Equal("refused", refused.GetProperty("state").GetString());
        Assert.Contains("waited longer than", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
        var failed = Asked(items, "[budget] Spend more.");
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.StartsWith("Budget has been exceeded!", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.True(failed.GetProperty("durationMs").GetInt32() >= 0);

        // Searched by state.
        var onlyFailed = await LogAsync(admin, $"person={name}&state=failed");
        Assert.Equal("[budget] Spend more.", Assert.Single(onlyFailed).GetProperty("question").GetString());
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task A_chat_question_is_in_the_log_with_its_answers_state_and_why_it_failed()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptchat_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "pq" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Which port does Postgres use?" })).Content.ReadAsStringAsync();
        await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "[budget] And MySQL?" })).Content.ReadAsStringAsync();

        var items = await LogWhenAsync(admin, $"person={name}&source=chat", i => StateOf(i, "[budget] And MySQL?") == "failed");
        Assert.Equal("done", StateOf(items, "Which port does Postgres use?"));
        var failed = Asked(items, "[budget] And MySQL?");
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Equal("You have used all your credit. Ask an admin to raise it.", failed.GetProperty("error").GetString());
        Assert.Equal("[budget] And MySQL?", Assert.Single(await LogAsync(admin, $"person={name}&source=chat&state=failed")).GetProperty("question").GetString());
        Assert.Empty(await LogAsync(admin, $"person={name}&source=chat&state=refused"));
    }

    [Fact]
    [Trait("Control", "DAT-08")]
    public async Task With_the_prompt_log_off_API_requests_are_not_kept()
    {
        await using var f = app.Create(app.ConnectionStringFor("promptsoff_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Gateway:LogPrompts"] = "false" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "po" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        await StatusAssert.Is(HttpStatusCode.OK, await AskAsync(f, made.GetProperty("apiKey").GetString()!, "Not to be kept."));
        await Task.Delay(500);
        Assert.Empty((await admin.JsonAsync(await admin.GetAsync($"/api/admin/prompts?person={name}&source=api"))).GetProperty("items").EnumerateArray());
    }
}
