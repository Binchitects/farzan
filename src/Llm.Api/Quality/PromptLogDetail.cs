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

    /// <summary>The most steps a run lists.</summary>
    public const int MostSteps = 2000;

    public static void MapPromptLogDetail(this RouteGroupBuilder g)
    {
        g.MapGet("/api/{id:guid}/detail", ApiAsync);
        g.MapGet("/chat/{id:guid}/detail", ChatAsync);
        g.MapGet("/runs/{chat:guid}", RunAsync);
    }

    private static string? Cut(string? s) => s is null ? null : s.Length <= Most ? s : s[..Most] + $"… ({s.Length - Most:N0} more characters)";

    /// <summary>A tool call as the page shows it: its id, its tool and its arguments as the model wrote them.</summary>
    public sealed record ToolCall(string? Id, string? Name, string? Arguments);

    private static List<ToolCall>? Calls(JsonNode? calls) => calls is not JsonArray list ? null : [.. list.Select(c => new ToolCall(
        c?["id"]?.ToString(), c?["function"]?["name"]?.ToString() ?? c?["name"]?.ToString(),
        Cut(c?["function"]?["arguments"]?.ToString() ?? c?["arguments"]?.ToJsonString())))];

    /// <summary>A message as the page shows it: its role, words (or parts' words), the tool it answers, and the calls it makes.</summary>
    private static object Message(JsonNode? m) => new
    {
        role = m?["role"]?.ToString(),
        content = Cut(m?["content"] switch
        {
            JsonValue v => v.ToString(),
            JsonArray parts => string.Join("\n", parts.Select(p => p?["type"]?.ToString() == "text" ? p["text"]?.ToString() : $"({p?["type"]})")),
            _ => null,
        }),
        name = m?["name"]?.ToString(), toolCallId = m?["tool_call_id"]?.ToString(), reasoning = Cut(m?["reasoning_content"]?.ToString()),
        toolCalls = Calls(m?["tool_calls"]),
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
        return Results.Ok(new
        {
            source = "api", e.Id, e.At, e.Model, e.State, e.Error, e.DurationMs, e.PromptTokens, e.CompletionTokens, chat = e.ChatId, e.Thread,
            kept = body is not null,
            from = request?["from"]?.GetValue<int>(), total = request?["total"]?.GetValue<int>(),
            messages = (request?["messages"] as JsonArray ?? []).Select(Message),
            tools = (request?["tools"] as JsonArray ?? []).Select(t => t?.ToString()),
            settings = request?["settings"],
            answer = response is null ? null : new
            {
                finish = response["finish_reason"]?.ToString(), reasoning = Cut(message?["reasoning_content"]?.ToString()), content = Cut(message?["content"]?.ToString()),
                toolCalls = Calls(message?["tool_calls"]), usage = response["usage"],
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
        return Results.Ok(new
        {
            source = "chat", id = q.Id, at = q.CreatedAt, chat = q.ConversationId, question = Cut(q.Content), quote = q.Quote, skill = q.Skill,
            steps = rounds.Select(m => new
            {
                m.Id, m.Role, at = m.CreatedAt, m.Model, content = Cut(m.Content), reasoning = Cut(m.Reasoning),
                toolCalls = m.ToolCallsJson is { } calls ? Calls(JsonNode.Parse(calls)) : null, tool = m.ToolName, toolCallId = m.ToolCallId,
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
        return Results.Ok(new
        {
            chat, title = c?.Title, kind = c is null ? null : c.Origin == Chat.CodeArenaSync.Origin ? "session" : "chat",
            threads = threads.Select((t, i) => new { id = t, name = i == 0 ? "Main" : $"Sub-agent {i}" }),
            steps = steps.Select(s => new
            {
                s.Id, s.At, thread = s.Thread ?? "", s.Model, s.State, s.Error, s.DurationMs, tokens = (s.PromptTokens ?? 0) + (s.CompletionTokens ?? 0),
                // "Called a, b": the tools it called; else its words' start.
                said = (string?)(s.Answer is { Length: > 160 } a ? a[..160] + "…" : s.Answer),
            }),
            questions,
        });
    }
}
