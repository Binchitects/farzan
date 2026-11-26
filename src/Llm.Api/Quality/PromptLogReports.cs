using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Quality;

/// <summary>What to write about a filtered log: the reader's question, the model to write it with, and the log's filters.</summary>
public sealed record LogReportRequest(string? Ask, string? Model = null, string? Q = null, string? Person = null, string? Source = null, string? State = null, string? Tag = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? SearchModel = null, Guid? Project = null, string? Tool = null, Guid? Run = null);

/// <summary>
/// A report written on a filtered prompt log, with a model the person chooses (one they may use, as them: their credit and
/// safeguards): the lines the filters find (at most <see cref="MostLines"/>, newest first), read a part at a time (each
/// part's notes on what it shows for the question asked), then one report from the notes. For prompts.review only, as the
/// log itself, and audited: who, which filters, which model, how many lines. Its progress and the report come as events.
/// </summary>
public static class PromptLogReports
{
    public const int MostLines = 2000;

    /// <summary>A part of the log the model reads at once, in characters (about 6,000 tokens).</summary>
    public const int PartChars = 24_000;

    public static void MapPromptLogReports(this RouteGroupBuilder g) => g.MapPost("/write", WriteAsync);

    /// <summary>A line of the log as the model reads it: when, who, where, the model and how it ended, the question and the answer.</summary>
    internal static string Line(PromptLogEndpoints.Row r, string who)
    {
        static string Cut(string? s, int most) => s is null ? "" : s.Length <= most ? s : s[..most] + "…";
        return $"[{r.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}] {who} ({r.Source}{(r.Model is { } m ? ", " + m : "")}, {r.State}"
            + $"{(r.Tokens is { } t ? $", {t} tokens" : "")}{(r.Title is { } title ? $", chat \"{Cut(title, 80)}\"" : "")})\nQ: {Cut(r.Question, 700)}\n"
            + (r.Answer is { } a ? $"A: {Cut(a, 500)}\n" : "") + (r.Error is { } e ? $"Error: {Cut(e, 200)}\n" : "");
    }

    /// <summary>The lines cut into parts of at most <see cref="PartChars"/> characters, none split.</summary>
    public static List<string> Parts(IEnumerable<string> lines)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        foreach (var line in lines)
        {
            if (part.Length > 0 && part.Length + line.Length > PartChars)
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            part.Append(line).Append('\n');
        }
        if (part.Length > 0)
        {
            parts.Add(part.ToString());
        }
        return parts;
    }

    private static async Task SendAsync(HttpResponse res, object e, CancellationToken ct)
    {
        await res.WriteAsync("data: " + JsonSerializer.Serialize(e, Chat.Mcp.Plain) + "\n\n", ct);
        await res.Body.FlushAsync(ct);
    }

    private static async Task WriteAsync(LogReportRequest body, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, Core.Data.AppDbContext db, Chat.AnswerJobs jobs,
        Chat.OneQuestion one, Audit audit, TimeProvider clock, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var ask = body.Ask?.Trim() ?? "";
        if (ask.Length is 0 or > 2000)
        {
            await Endpoints.AuthEndpoints.Problem(400, "ask", "Say what the report is about, up to 2,000 characters.").ExecuteAsync(http);
            return;
        }
        var filter = new PromptLogEndpoints.LogFilter(body.Q, body.Person, body.Source, body.State, body.Tag, body.From, body.To, null, body.SearchModel, body.Project, body.Tool, body.Run);
        var (rows, problem) = await PromptLogEndpoints.FindAsync(filter, MostLines, db, jobs, clock, ct);
        if (problem is not null)
        {
            await problem.ExecuteAsync(http);
            return;
        }
        var people = rows!.Select(r => r.Person).Distinct().ToList();
        var names = db.Users.Where(u => people.Contains(u.Id)).Select(u => new { u.Id, u.UserName }).ToDictionary(u => u.Id, u => u.UserName ?? "someone");
        var lines = rows!.OrderBy(r => r.At).Select(r => Line(r, names.GetValueOrDefault(r.Person) ?? "someone removed")).ToList();
        var said = string.Join("; ", new[]
        {
            body.Q is { Length: > 0 } q ? $"words \"{q}\"" : null, body.Person is { Length: > 0 } who ? $"person {who}" : null, body.Source is "chat" or "api" ? $"source {body.Source}" : null,
            body.State is { Length: > 0 } st && st != "all" ? $"state {st}" : null, body.Tag is { Length: > 0 } tag ? $"tag {tag}" : null, body.SearchModel is { Length: > 0 } sm ? $"model {sm}" : null,
            body.Project is { } pr ? $"project {pr}" : null, body.Tool is { Length: > 0 } tool ? $"tool {tool}" : null, body.Run is { } run ? $"run {run}" : null,
            body.From is { } f ? $"from {f:O}" : null, body.To is { } t ? $"to {t:O}" : null,
        }.OfType<string>());
        await audit.WriteAsync("prompts.report.written", null, detail: $"{lines.Count} lines; {(said.Length > 0 ? said : "no filter")}; model {body.Model ?? "default"}; asked: {(ask.Length > 300 ? ask[..300] + "…" : ask)}");

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        if (lines.Count == 0)
        {
            await SendAsync(http.Response, new { type = "error", message = "No line of the log matches these filters." }, ct);
            return;
        }
        var parts = Parts(lines);
        var window = $"{rows!.Min(r => r.At):yyyy-MM-dd} to {rows!.Max(r => r.At):yyyy-MM-dd}";
        var notes = new List<string>();
        for (var i = 0; i < parts.Count && parts.Count > 1; i++)
        {
            await SendAsync(http.Response, new { type = "progress", done = i, of = parts.Count + 1, lines = lines.Count }, ct);
            var noted = await one.AskAsync(me, parts[i], $"""
                You read part {i + 1} of {parts.Count} of a log of questions people asked an AI assistant and the answers they got (each line: when, who,
                where, the model, how it ended, the question, the answer). The reader wants a report on this: "{ask}".
                Write notes on what this part shows for that: facts with counts, who and when, short quotes as examples, what went wrong. At most 300 words.
                Only what the lines say; no advice yet.
                """, body.Model, 900, "log-report", ct);
            if (noted.Problem is not null)
            {
                await SendAsync(http.Response, new { type = "error", message = "The model could not read a part of the log. Choose another model, or fewer lines." }, ct);
                return;
            }
            notes.Add($"Part {i + 1} ({parts[i].Count(c => c == '[')} lines):\n{noted.Text}");
        }
        await SendAsync(http.Response, new { type = "progress", done = parts.Count, of = parts.Count + 1, lines = lines.Count }, ct);
        var source = parts.Count == 1 ? parts[0] : string.Join("\n\n", notes);
        var written = await one.AskAsync(me, source, $"""
            Write the report the reader asked for: "{ask}".
            It is on {lines.Count} lines of a prompt log of an AI assistant (questions people asked and the answers), {window}{(said.Length > 0 ? $"; filtered by {said}" : "")}.
            {(parts.Count == 1 ? "Here are the lines." : "Here are notes on its parts, read one at a time.")}
            In Markdown: a title, a summary of a few sentences, then sections with what was found (numbers, who, when, short quotes as examples),
            then what to do about it. Say plainly what the log cannot tell. Write in the language the reader wrote in.
            """, body.Model, 3000, "log-report", ct);
        if (written.Problem is not null)
        {
            await SendAsync(http.Response, new { type = "error", message = "The model could not write the report. Choose another model." }, ct);
            return;
        }
        await SendAsync(http.Response, new { type = "report", markdown = written.Text, lines = lines.Count, parts = parts.Count, window, filters = said }, ct);
    }
}
