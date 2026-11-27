// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Quality;

/// <summary>
/// One line of the prompt log in full, for prompts.review (each look audited): an API request's messages (those new since its
/// thread's step before, the tool results among them), the tools it offered and its settings, and its answer: the whole
/// thinking, the words, each tool call with its arguments; a chat question's answer round by round (each round's thinking,
/// words and tool calls, each tool's result, the sub-agents' work); and a run, every step of a chat or a Code Arena session
/// in order, by thread (the main agent, and each sub-agent with its own start).
/// </summary>
public static class PromptLogDetail
{
    /// <summary>The most of one tool's result, one message or one thinking a detail carries (the rest is said to be cut).</summary>
    public const int Most = 200_000;

    /// <summary>The most of a call's arguments or result a run's step shows.</summary>
    public const int RunMost = 2000;

    /// <summary>The most steps a run lists.</summary>
    public const int MostSteps = 2000;

    public static void MapPromptLogDetail(this RouteGroupBuilder g)
    {
        g.MapGet("/api/{id:guid}/detail", ApiAsync);
        g.MapGet("/chat/{id:guid}/detail", ChatAsync);
        g.MapGet("/runs/{chat:guid}", RunAsync);
    }

    private static string? Cut(string? s) => s is null ? null : s.Length <= Most ? s : s[..Most] + $"… ({s.Length - Most:N0} more characters)";

    /// <summary>
    /// A tool call as the page shows it: its id, its tool, its arguments as the model wrote them, and what the tool gave back
    /// (null while no later step of its thread carried the result).
    /// </summary>
    public sealed record ToolCall(string? Id, string? Name, string? Arguments, string? Result = null);

    private static List<ToolCall>? Calls(JsonNode? calls, IReadOnlyDictionary<string, string>? results = null, Func<string?, string?>? cut = null) =>
        calls is not JsonArray list ? null : [.. list.Select(c =>
        {
            var id = c?["id"]?.ToString();
            cut ??= Cut;
            return new ToolCall(id, c?["function"]?["name"]?.ToString() ?? c?["name"]?.ToString(),
                cut(c?["function"]?["arguments"]?.ToString() ?? c?["arguments"]?.ToJsonString()),
                id is not null && results is not null && results.TryGetValue(id, out var r) ? cut(r) : null);
        })];

    /// <summary>The words of a message's content: its text, or its parts' words.</summary>
    private static string? Words(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray parts => string.Join("\n", parts.Select(p => p?["type"]?.ToString() == "text" ? p["text"]?.ToString() : $"({p?["type"]})")),
        _ => null,
    };

    /// <summary>The tool results a kept request carried (its role "tool" messages), by the call they answer.</summary>
    private static void ResultsOf(JsonNode? request, Dictionary<string, string> into)
    {
        foreach (var m in (request?["messages"] as JsonArray ?? []).OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool"))
        {
            if (m["tool_call_id"]?.ToString() is { Length: > 0 } id)
            {
                into.TryAdd(id, Words(m["content"]) ?? "");
            }
        }
    }

    /// <summary>How many later steps of a thread are read for its calls' results (the next, usually; a few, for a retried step).</summary>
    private const int ResultSteps = 4;

    /// <summary>A message as the page shows it: its role, words (or parts' words), the tool it answers, and the calls it makes.</summary>
    private static object Message(JsonNode? m, IReadOnlyDictionary<string, string> results) => new
    {
        role = m?["role"]?.ToString(),
        content = Cut(Words(m?["content"])),
        name = m?["name"]?.ToString(), toolCallId = m?["tool_call_id"]?.ToString(), reasoning = Cut(m?["reasoning_content"]?.ToString()),
        toolCalls = Calls(m?["tool_calls"], results),
    };

    private static async Task<IResult> ApiAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.PromptLog.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is not { } e)
        {
            return Results.NotFound();
        }
        var body = await db.PromptLogBodies.AsNoTracking().SingleOrDefaultAsync(b => b.EntryId == id, ct);
        var request = PromptLogWriter.Unzip(body?.Request);
        var response = PromptLogWriter.Unzip(body?.Response);
        await audit.WriteAsync("prompts.detail.viewed", id.ToString(), detail: $"api {e.Model}");
        var message = response?["message"];
        // The calls among what it sent, each with its result when this request carried that too.
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        ResultsOf(request, sent);
        // Its calls' results: what the tools gave back, sent with the thread's next step (the same person, run and thread).
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        if (message?["tool_calls"] is JsonArray { Count: > 0 } && e.Thread is not null)
        {
            var later = await db.PromptLog.AsNoTracking().Where(p => p.UserId == e.UserId && p.Thread == e.Thread && p.ChatId == e.ChatId && p.At > e.At)
                .OrderBy(p => p.At).Take(ResultSteps)
                .Join(db.PromptLogBodies, p => p.Id, b => b.EntryId, (p, b) => b.Request).ToListAsync(ct);
            foreach (var zipped in later)
            {
                ResultsOf(PromptLogWriter.Unzip(zipped), results);
            }
        }
        return Results.Ok(new
        {
            source = "api", e.Id, e.At, e.Model, e.State, e.Error, e.DurationMs, e.PromptTokens, e.CompletionTokens, chat = e.ChatId, e.Thread,
            kept = body is not null,
            from = request?["from"]?.GetValue<int>(), total = request?["total"]?.GetValue<int>(),
            messages = (request?["messages"] as JsonArray ?? []).Select(m => Message(m, sent)),
            tools = (request?["tools"] as JsonArray ?? []).Select(t => t?.ToString()),
            settings = request?["settings"],
            answer = response is null ? null : new
            {
                finish = response["finish_reason"]?.ToString(), reasoning = Cut(message?["reasoning_content"]?.ToString()), content = Cut(message?["content"]?.ToString()),
                toolCalls = Calls(message?["tool_calls"], results), usage = response["usage"],
            },
        });
    }

    /// <summary>A chat question's answer, round by round, from the chat itself: what the model thought, said and called, each tool's result.</summary>
    private static async Task<IResult> ChatAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id && m.Role == "user", ct) is not { } q)
        {
            return Results.NotFound();
        }
        var first = await db.ChatMessages.AsNoTracking().Where(a => a.ConversationId == q.ConversationId && a.ParentId == id && a.Role == "assistant")
            .OrderBy(a => a.Sequence).Select(a => a.Id).FirstOrDefaultAsync(ct);
        var rounds = first == Guid.Empty ? [] : await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == q.ConversationId && (m.Id == first || m.AnswerId == first))
            .OrderBy(m => m.Sequence).ToListAsync(ct);
        await audit.WriteAsync("prompts.detail.viewed", id.ToString(), detail: "chat");
        // Each call's result beside it: the round's tool messages, by the call they answer.
        var said = rounds.Where(m => m.Role == "tool" && m.ToolCallId is not null).GroupBy(m => m.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Content, StringComparer.Ordinal);
        return Results.Ok(new
        {
            source = "chat", id = q.Id, at = q.CreatedAt, chat = q.ConversationId, question = Cut(q.Content), quote = q.Quote, skill = q.Skill,
            steps = rounds.Select(m => new
            {
                m.Id, m.Role, at = m.CreatedAt, m.Model, content = Cut(m.Content), reasoning = Cut(m.Reasoning),
                toolCalls = m.ToolCallsJson is { } calls ? Calls(JsonNode.Parse(calls), said) : null, tool = m.ToolName, toolCallId = m.ToolCallId,
                // A tool call's sub-agents' work (their thinking, tool calls and words), as the chat shows it.
                details = m.DetailsJson is { } d ? JsonNode.Parse(d) : null,
                m.PromptTokens, m.CompletionTokens, m.Status, m.Error,
            }),
        });
    }

    /// <summary>
    /// A run: every API step of a chat or a Code Arena session, in order, each with its thread (Main: the first; then Sub-agent 1,
    /// 2...), the tools it called and how it ended; and, for a chat of the app, its questions (read in full from the chat).
    /// </summary>
    private static async Task<IResult> RunAsync(Guid chat, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var steps = await db.PromptLog.AsNoTracking().Where(p => p.ChatId == chat).OrderBy(p => p.At).Take(MostSteps)
            .Select(p => new { p.Id, p.At, p.Thread, p.Model, p.State, p.Error, p.DurationMs, p.PromptTokens, p.CompletionTokens, p.Answer, p.Prompt }).ToListAsync(ct);
        var threads = steps.Select(s => s.Thread ?? "").Distinct().ToList();
        var c = await db.Conversations.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == chat).Select(x => new { x.Title, x.Origin, x.UserId }).SingleOrDefaultAsync(ct);
        var questions = (await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == chat && m.Role == "user").OrderBy(m => m.Sequence)
            .Select(m => new { m.Id, at = m.CreatedAt, m.Content }).Take(MostSteps).ToListAsync(ct))
            .Select(m => new { m.Id, m.at, text = m.Content.Length > 300 ? m.Content[..300] + "…" : m.Content }).ToList();
        await audit.WriteAsync("prompts.run.viewed", chat.ToString(), detail: $"{steps.Count} steps");
        // Each step's tool calls with their arguments, and each one's result (from its thread's next step), short: the step opened has them whole.
        var ids = steps.Select(s => s.Id).ToList();
        var bodies = new Dictionary<Guid, (JsonNode? Request, JsonNode? Response)>();
        foreach (var chunk in ids.Chunk(200))
        {
            foreach (var b in await db.PromptLogBodies.AsNoTracking().Where(b => chunk.Contains(b.EntryId)).ToListAsync(ct))
            {
                bodies[b.EntryId] = (PromptLogWriter.Unzip(b.Request), PromptLogWriter.Unzip(b.Response));
            }
        }
        // By thread: call ids are the model's, and two threads may both have a call_0.
        var results = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (bodies.TryGetValue(s.Id, out var b))
            {
                var thread = s.Thread ?? "";
                if (!results.TryGetValue(thread, out var of))
                {
                    results[thread] = of = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                ResultsOf(b.Request, of);
            }
        }
        string? Short(string? x) => x is null ? null : x.Length <= RunMost ? x : x[..RunMost] + $"… ({x.Length - RunMost:N0} more characters: open the step)";
        return Results.Ok(new
        {
            chat, title = c?.Title, kind = c is null ? null : c.Origin == Chat.CodeArenaSync.Origin ? "session" : "chat",
            threads = threads.Select((t, i) => new { id = t, name = i == 0 ? "Main" : $"Sub-agent {i}" }),
            steps = steps.Select(s => new
            {
                s.Id, s.At, thread = s.Thread ?? "", s.Model, s.State, s.Error, s.DurationMs, tokens = (s.PromptTokens ?? 0) + (s.CompletionTokens ?? 0),
                // "Called a, b": the tools it called; else its words' start.
                said = (string?)(s.Answer is { Length: > 160 } a ? a[..160] + "…" : s.Answer),
                calls = bodies.TryGetValue(s.Id, out var b) ? Calls(b.Response?["message"]?["tool_calls"], results.GetValueOrDefault(s.Thread ?? ""), Short) : null,
            }),
            questions,
        });
    }
}
