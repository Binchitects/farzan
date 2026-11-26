using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>
/// Keeps each API request in the prompt log (Gateway:LogPrompts) from the moment it arrives: waiting for its turn, running,
/// then done, failed, stopped (the caller went away) or refused, with its answer or why it failed. The writes never hold a
/// request up: they run behind it in order, and one that fails is logged and dropped.
/// </summary>
public sealed partial class PromptLogWriter(IServiceScopeFactory scopes, TimeProvider clock, Microsoft.Extensions.Options.IOptionsMonitor<AnswerCacheOptions> options,
    ILogger<PromptLogWriter> logger)
{
    /// <summary>The question a request asks: its last user message's text (its text parts, for a message of parts).</summary>
    public static string? LastQuestion(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages || messages.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user") is not JsonObject last)
        {
            return null;
        }
        return last["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var text) => text,
            JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "text").Select(p => p["text"]?.GetValue<string>()).OfType<string>()),
            _ => null,
        };
    }

    /// <summary>
    /// The pictures a request's last question carried as data (its image_url parts: data:TYPE;base64,...), at most
    /// <see cref="PromptLogFile.MaxFiles"/>, each at most <see cref="PromptLogFile.MaxBytes"/>; a picture by address is not fetched.
    /// </summary>
    public static IReadOnlyList<PromptLogFile> PicturesOf(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages || messages.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user") is not JsonObject { } last
            || last["content"] is not JsonArray parts)
        {
            return [];
        }
        var pictures = new List<PromptLogFile>();
        foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "image_url"))
        {
            var url = part["image_url"] is JsonObject o ? o["url"]?.GetValue<string>() : part["image_url"]?.GetValue<string>();
            var at = url?.IndexOf(";base64,", StringComparison.Ordinal) ?? -1;
            if (url is null || !url.StartsWith("data:", StringComparison.Ordinal) || at < 0)
            {
                continue;
            }
            var type = url[5..at];
            byte[] data;
            try
            {
                data = Convert.FromBase64String(url[(at + 8)..]);
            }
            catch (FormatException)
            {
                continue;
            }
            if (data.Length > PromptLogFile.MaxBytes || !type.StartsWith("image/", StringComparison.Ordinal) || type.Length > 100)
            {
                continue;
            }
            var extension = type[6..] switch { "jpeg" => "jpg", "svg+xml" => "svg", var t when t.All(char.IsAsciiLetterOrDigit) => t, _ => "img" };
            pictures.Add(new PromptLogFile { Name = $"picture-{pictures.Count + 1}.{extension}", ContentType = type, Data = data });
            if (pictures.Count == PromptLogFile.MaxFiles)
            {
                break;
            }
        }
        return pictures;
    }

    /// <summary>What a completion said: its words, else the tools it called.</summary>
    public static string? AnswerOf(JsonObject? completion)
    {
        var message = completion?["choices"]?[0]?["message"];
        return message?["content"]?.GetValue<string>() is { Length: > 0 } said ? said
            : message?["tool_calls"] is JsonArray calls && calls.Count > 0 ? "Called " + string.Join(", ", calls.Select(c => c?["function"]?["name"]?.GetValue<string>()).OfType<string>()) : null;
    }

    /// <summary>The message of an OpenAI-style error body (<c>{"error": {"message": ...}}</c>), else the body itself, short.</summary>
    public static string? ErrorOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(body)?["error"] is { } error)
            {
                return Short(error is JsonValue v && v.TryGetValue<string>(out var text) ? text : error["message"]?.GetValue<string>() ?? error.ToJsonString());
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: the text as it came.
        }
        return Short(body.Trim());
    }

    private static string Short(string s) => s.Length <= 2000 ? s : s[..1999] + "…";

    /// <summary>How much of a question or an answer is kept (Gateway:PromptLogKeep), at most <see cref="PromptLogEntry.MaxText"/>.</summary>
    private int Keep => Math.Clamp(options.CurrentValue.PromptLogKeep, 200, PromptLogEntry.MaxText);

    private string Cut(string s) => s.Length <= Keep ? s : s[..(Keep - 1)] + "…";

    /// <summary>Whether this model's requests are kept (Gateway:PromptLogModels: one a line or comma; empty: every model).</summary>
    public bool Logs(string? model)
    {
        var only = AnswerCacheOptions.Split(options.CurrentValue.PromptLogModels);
        return only.Count == 0 || (model is { Length: > 0 } && only.Contains(model, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One request's line in the log, written as it goes: its writes run one after another, the first adding it. Null-safe
    /// to use: a request that is not logged has none.
    /// </summary>
    public sealed class Line
    {
        private readonly PromptLogWriter _writer;
        private readonly Lock _lock = new();
        private Task _last;
        private bool _ended;

        internal Line(PromptLogWriter writer, PromptLogEntry entry, IReadOnlyList<PromptLogFile> pictures, byte[]? body = null)
        {
            _writer = writer;
            Entry = entry;
            Started = writer.Clock.GetTimestamp();
            _last = writer.WriteAsync(db =>
            {
                db.PromptLog.Add(entry);
                // The request in full: what it sent, zipped (its answer joins it as it ends).
                if (body is not null)
                {
                    db.PromptLogBodies.Add(new PromptLogBody { EntryId = entry.Id, Request = body });
                }
                // The question's pictures go in with it, and go with it (the line's retention).
                foreach (var picture in pictures)
                {
                    picture.EntryId = entry.Id;
                    db.PromptLogFiles.Add(picture);
                }
                return db.SaveChangesAsync();
            });
        }

        public PromptLogEntry Entry { get; }

        private long Started { get; }

        /// <summary>Its turn came: it is with the model now.</summary>
        public void Running() => Then(PromptLogEntry.Running, null, null, 0);

        /// <summary>Its end: done, failed, stopped or refused, with what was answered and why it failed. Only the first end counts.</summary>
        public void End(string state, int status, string? answer = null, string? error = null, JsonObject? completion = null)
        {
            lock (_lock)
            {
                if (_ended)
                {
                    return;
                }
                _ended = true;
            }
            Then(state, answer ?? AnswerOf(completion), error, status, completion);
        }

        private void Then(string state, string? answer, string? error, int status, JsonObject? completion = null)
        {
            var id = Entry.Id;
            var ms = (int)Math.Min(int.MaxValue, _writer.Clock.GetElapsedTime(Started).TotalMilliseconds);
            var done = state is not (PromptLogEntry.Waiting or PromptLogEntry.Running);
            int? promptTokens = completion?["usage"]?["prompt_tokens"]?.GetValue<int>();
            int? completionTokens = completion?["usage"]?["completion_tokens"]?.GetValue<int>();
            var said = answer is null ? null : _writer.Cut(answer);
            var why = error is null ? null : Short(error);
            // The answer in full, zipped: its thinking, words and tool calls with their arguments.
            var whole = done && completion is not null && _writer.Full ? Response(completion) : null;
            lock (_lock)
            {
                _last = _last.ContinueWith(_ => _writer.WriteAsync(async db =>
                {
                    await db.PromptLog.Where(p => p.Id == id).ExecuteUpdateAsync(u =>
                {
                    u.SetProperty(p => p.State, state).SetProperty(p => p.Status, status);
                    if (said is not null)
                    {
                        u.SetProperty(p => p.Answer, said);
                    }
                    if (why is not null)
                    {
                        u.SetProperty(p => p.Error, why);
                    }
                    if (promptTokens is not null)
                    {
                        u.SetProperty(p => p.PromptTokens, promptTokens);
                    }
                    if (completionTokens is not null)
                    {
                        u.SetProperty(p => p.CompletionTokens, completionTokens);
                    }
                    if (done)
                    {
                        u.SetProperty(p => p.DurationMs, ms);
                    }
                });
                    if (whole is not null)
                    {
                        await db.PromptLogBodies.Where(b => b.EntryId == id).ExecuteUpdateAsync(u => u.SetProperty(b => b.Response, whole));
                    }
                }), TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>
        /// Ends it, when nothing else did, as the request ends (dispose at the end of the request): stopped when the caller
        /// went away, else failed. A request ended on any path is never left running in the log.
        /// </summary>
        public IDisposable Closing(CancellationToken caller) => new Closer(this, caller);

        private sealed class Closer(Line line, CancellationToken caller) : IDisposable
        {
            public void Dispose() => line.End(caller.IsCancellationRequested ? PromptLogEntry.Stopped : PromptLogEntry.Failed, caller.IsCancellationRequested ? 499 : 500,
                error: caller.IsCancellationRequested ? "The caller went away before the answer." : "The request ended without an answer.");
        }

        /// <summary>For tests: every write so far has run.</summary>
        public Task Written
        {
            get
            {
                lock (_lock)
                {
                    return _last;
                }
            }
        }
    }

    private TimeProvider Clock => clock;

    /// <summary>Gateway:PromptLogFull: each request kept whole (its messages, tools and answer), not only its last question.</summary>
    private bool Full => options.CurrentValue.PromptLogFull;

    /// <summary>The run a request is a step of: its chat:ID tag (Code Arena's, the chat's), if it has one.</summary>
    public static Guid? ChatOf(JsonObject request) =>
        (request["metadata"]?["tags"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var x) ? x : null)
            .FirstOrDefault(t => t?.StartsWith(Projects.ProjectTags.ChatPrefix, StringComparison.Ordinal) == true) is { } tag && Guid.TryParse(tag[Projects.ProjectTags.ChatPrefix.Length..], out var g) ? g : null;

    /// <summary>A thread of a run: its first two messages' print (the system's and the first question), the same for each of its steps.</summary>
    public static string ThreadOf(JsonArray messages) =>
        Print(string.Concat(messages.Take(2).Select(m => (m?["role"]?.ToString() ?? "") + "\u0001" + (m?["content"]?.ToJsonString() ?? "") + "\u0002")))[..16];

    private static string Print(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Each thread's last step: how many messages it sent and their print, so the next keeps only the new ones.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, string Print)> _threads = new(StringComparer.Ordinal);

    /// <summary>
    /// A request as kept whole: its settings and tools, and its messages from <c>from</c> on (those its thread's step before
    /// did not send, when that step's are the start of these; else all), pictures named, not kept again. Zipped.
    /// </summary>
    internal (byte[] Body, int From, string Thread) Request(Guid person, Guid? chat, JsonObject request)
    {
        var messages = request["messages"] as JsonArray ?? [];
        var thread = ThreadOf(messages);
        var texts = messages.Select(m => m?.ToJsonString() ?? "null").ToList();
        var key = $"{person}:{chat}:{thread}";
        var from = 0;
        if (_threads.TryGetValue(key, out var before) && before.Count <= texts.Count && Print(string.Concat(texts.Take(before.Count))) == before.Print)
        {
            from = before.Count;
        }
        if (_threads.Count > 20_000)
        {
            _threads.Clear();
        }
        _threads[key] = (texts.Count, Print(string.Concat(texts)));
        var kept = new JsonArray([.. messages.Skip(from).Select(m => (JsonNode?)Unpictured(m?.DeepClone()))]);
        var tools = new JsonArray([.. (request["tools"] as JsonArray ?? []).Select(t => (JsonNode?)(t?["function"]?["name"]?.ToString() ?? t?["name"]?.ToString()))]);
        var settings = new JsonObject();
        foreach (var name in new[] { "temperature", "top_p", "max_tokens", "max_completion_tokens", "stream", "tool_choice", "reasoning_effort", "response_format" })
        {
            if (request[name] is { } v)
            {
                settings[name] = v.DeepClone();
            }
        }
        var body = new JsonObject { ["model"] = request["model"]?.DeepClone(), ["from"] = from, ["total"] = texts.Count, ["messages"] = kept, ["tools"] = tools, ["settings"] = settings };
        return (Zip(body), from, thread);
    }

    /// <summary>An answer as kept whole: why it ended, its message (thinking, words, tool calls) and its usage. Zipped.</summary>
    internal static byte[] Response(JsonObject completion)
    {
        var choice = completion["choices"]?[0];
        return Zip(new JsonObject
        {
            ["finish_reason"] = choice?["finish_reason"]?.DeepClone(), ["message"] = choice?["message"]?.DeepClone(), ["usage"] = completion["usage"]?.DeepClone(),
        });
    }

    /// <summary>A message with its pictures as data named, not kept (the question's are kept apart, as files).</summary>
    private static JsonNode? Unpictured(JsonNode? m)
    {
        foreach (var part in (m?["content"] as JsonArray ?? []).OfType<JsonObject>().Where(p => p["type"]?.ToString() == "image_url"))
        {
            part["image_url"] = new JsonObject { ["url"] = "(a picture)" };
        }
        return m;
    }

    public static byte[] Zip(JsonNode body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        if (bytes.Length > PromptLogBody.MaxBytes)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["cut"] = true, ["bytes"] = bytes.Length }.ToJsonString());
        }
        using var m = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(m, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(bytes);
        }
        return m.ToArray();
    }

    /// <summary>A kept body read back.</summary>
    public static JsonNode? Unzip(byte[]? zipped)
    {
        if (zipped is null)
        {
            return null;
        }
        using var z = new System.IO.Compression.GZipStream(new MemoryStream(zipped), System.IO.Compression.CompressionMode.Decompress);
        return JsonNode.Parse(z);
    }

    /// <summary>A request arrived: in the log at once, waiting for its turn. Null when it asks no question (nothing to show).</summary>
    public Line? Begin(Guid person, string? keyToken, JsonObject request)
    {
        var question = LastQuestion(request) ?? "";
        if (!Logs(request["model"]?.GetValue<string>()))
        {
            return null;
        }
        var pictures = options.CurrentValue.PromptLogPictures ? PicturesOf(request) : [];
        if (question.Length == 0 && pictures.Count == 0)
        {
            return null;
        }
        var chat = ChatOf(request);
        var (body, from, thread) = Full ? Request(person, chat, request) : (null, 0, null);
        return new Line(this, new PromptLogEntry
        {
            UserId = person, KeyToken = keyToken, Model = request["model"]?.GetValue<string>() ?? "",
            Prompt = question.Length > 0 ? Cut(question) : pictures.Count == 1 ? "(a picture)" : $"({pictures.Count} pictures)", Status = 0,
            State = PromptLogEntry.Waiting, At = clock.GetUtcNow(), ChatId = chat, Thread = thread, From = body is null ? null : from,
        }, pictures, body);
    }

    /// <summary>A request answered at once (from the answer cache): in the log done, with its answer.</summary>
    public void Record(Guid person, string? keyToken, JsonObject request, JsonObject? completion, int status) =>
        Begin(person, keyToken, request)?.End(status is >= 200 and < 300 ? PromptLogEntry.Done : PromptLogEntry.Failed, status, completion: completion);

    /// <summary>The writes under way, to wait for (tests).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _pending = new();

    /// <summary>Every write begun so far has run (tests).</summary>
    public async Task FlushAsync()
    {
        while (_pending.Keys.ToList() is { Count: > 0 } now)
        {
            await Task.WhenAll(now);
        }
    }

    private Task WriteAsync(Func<AppDbContext, Task> write)
    {
        var task = WriteNowAsync(write);
        _pending[task] = 0;
        _ = task.ContinueWith(t => _pending.TryRemove(t, out _), TaskScheduler.Default);
        return task;
    }

    private async Task WriteNowAsync(Func<AppDbContext, Task> write)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await write(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An API request's line in the prompt log was not written: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
