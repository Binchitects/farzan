using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing.Template;
using Permissions = Llm.Core.Access.Permissions;

namespace Llm.Api.AdminHelp;

public sealed record AssistantTurn(string? Role, string? Content);

/// <summary>A question to the admin assistant: the conversation so far (the person's words and its answers), the admin page open, a model.</summary>
public sealed record AssistantAsk(List<AssistantTurn>? Messages, string? Page = null, string? Model = null);

/// <summary>A change the assistant proposed, kept until the person who asked says yes or no (at most <see cref="AssistantChanges.Kept"/>).</summary>
public sealed record Proposal(Guid Id, Guid Person, string Method, string Path, string? Body, string What, DateTimeOffset At);

/// <summary>The changes proposed and not yet answered, each for the person it was proposed to only.</summary>
public sealed class AssistantChanges(TimeProvider clock)
{
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, Proposal> proposals = new();

    public Proposal Add(Guid person, string method, string path, string? body, string what)
    {
        foreach (var old in proposals.Values.Where(p => clock.GetUtcNow() - p.At > Kept))
        {
            proposals.TryRemove(old.Id, out _);
        }
        var p = new Proposal(Guid.NewGuid(), person, method, path, body, what, clock.GetUtcNow());
        proposals[p.Id] = p;
        return p;
    }

    /// <summary>The proposal, taken (it is answered once), when it is this person's and not too old.</summary>
    public Proposal? Take(Guid id, Guid person) =>
        proposals.TryGetValue(id, out var p) && p.Person == person && proposals.TryRemove(id, out _) && clock.GetUtcNow() - p.At <= Kept ? p : null;
}

/// <summary>
/// The admin assistant, beside every admin page: it answers how to run Arena and does it, as the person signed in and no
/// more. Its tools are Arena's own API, sent through the app's pipeline as them (<see cref="AppPipeline"/>): it finds the
/// routes they may call, reads freely (GET), and a change (POST, PUT, PATCH, DELETE) only proposes: the page shows what it
/// will do, and it is done on the person's yes alone (<c>/changes/{id}/yes</c>), audited as them with the assistant named.
/// </summary>
public static class AdminAssistant
{
    public const int MostRounds = 12;
    public const int MostRead = 12_000;
    private const string Self = "/api/admin-assistant";

    public static void MapAdminAssistant(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Self).PersonOnly();
        g.MapPost("/ask", AskAsync);
        g.MapPost("/changes/{id:guid}/yes", YesAsync);
        g.MapPost("/changes/{id:guid}/no", NoAsync);
    }

    /// <summary>A route of the API as the assistant knows it: its method and pattern, the permission it needs, whether the person holds it, the body it takes.</summary>
    public sealed record Route(string Method, string Pattern, string? Permission, bool Mine, string? Body)
    {
        public override string ToString() => $"{Method} {Pattern}" + (Permission is { } p ? $" [{p}]" : "") + (Body is { } b ? $" body {b}" : "");
    }

    /// <summary>Every route of the API under /api that a person signed in may reach (not those open to all, nor the assistant's own).</summary>
    public static List<Route> Routes(EndpointDataSource source, Access.Membership me) =>
        [.. source.Endpoints.OfType<RouteEndpoint>()
            .Select(e => (e, pattern: "/" + (e.RoutePattern.RawText ?? "").Trim('/')))
            .Where(x => x.pattern.StartsWith("/api/", StringComparison.Ordinal) && !x.pattern.StartsWith(Self, StringComparison.Ordinal)
                && x.e.Metadata.GetMetadata<AnonymousMetadata>() is null)
            .SelectMany(x => (x.e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m =>
            {
                var permission = x.e.Metadata.GetMetadata<PermissionMetadata>()?.Permission;
                var mine = permission is null ? x.e.Metadata.GetMetadata<PersonOnlyMetadata>() is not null : me.Has(permission);
                var type = x.e.Metadata.OfType<IAcceptsMetadata>().FirstOrDefault()?.RequestType;
                return new Route(m, x.pattern, permission, mine, HttpMethods.IsGet(m) || type is null ? null : Shape(type, 0));
            }))
            .DistinctBy(r => (r.Method, r.Pattern))
            .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)];

    /// <summary>A body's JSON shape: its fields by their JSON names, with their kinds (a field that is an object, one level in).</summary>
    public static string Shape(Type t, int depth)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string) || t == typeof(Guid) || t.IsEnum)
        {
            return t == typeof(Guid) ? "id" : t.IsEnum ? string.Join("|", Enum.GetNames(t)) : "text";
        }
        if (t == typeof(bool))
        {
            return "true|false";
        }
        if (t.IsPrimitive || t == typeof(decimal))
        {
            return "number";
        }
        if (t == typeof(DateTimeOffset) || t == typeof(DateTime) || t == typeof(DateOnly))
        {
            return "time";
        }
        if (t.IsArray || (t.IsGenericType && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))))
        {
            var item = t.IsArray ? t.GetElementType()! : t.GetGenericArguments().Last();
            return $"[{Shape(item, depth + 1)}]";
        }
        if (depth > 1 || t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            return "object";
        }
        var fields = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .Select(p => $"{JsonNamingPolicy.CamelCase.ConvertName(p.Name)}: {Shape(p.PropertyType, depth + 1)}");
        return "{" + string.Join(", ", fields) + "}";
    }

    /// <summary>The person's routes whose pattern holds the most of these words (no words: the API's areas, how many routes each).</summary>
    public static string Find(List<Route> routes, string? words)
    {
        var mine = routes.Where(r => r.Mine).ToList();
        var wanted = (words ?? "").ToLowerInvariant().Split([' ', ',', '/', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd('s')).Where(w => w.Length > 1 && w != "api").Distinct().ToList();
        if (wanted.Count == 0)
        {
            return "Areas of the API you may use (find_api with words of one for its routes):\n" + string.Join("\n", mine
                .GroupBy(r => string.Join('/', r.Pattern.Split('/').Skip(2).TakeWhile(s => !s.StartsWith('{')).Take(r.Pattern.StartsWith("/api/admin/", StringComparison.Ordinal) ? 2 : 1)))
                .Select(g => $"/api/{g.Key} ({g.Count()} routes)"));
        }
        var found = mine.Select(r => (r, score: wanted.Count(w => r.Pattern.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.r.Pattern.Length).Take(40).Select(x => x.r).ToList();
        var notMine = routes.Count(r => !r.Mine && wanted.All(w => r.Pattern.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return (found.Count == 0 ? "No route of yours has those words." : string.Join("\n", found.OrderBy(r => r.Pattern, StringComparer.Ordinal)))
            + (notMine > 0 ? $"\n({notMine} more need a permission you do not hold.)" : "");
    }

    /// <summary>The routes this path and method would reach (by pattern; a parameter takes any one segment).</summary>
    public static List<Route> Matching(List<Route> routes, string method, string path)
    {
        var at = path.IndexOf('?', StringComparison.Ordinal);
        var bare = at < 0 ? path : path[..at];
        return [.. routes.Where(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
            && new TemplateMatcher(TemplateParser.Parse(r.Pattern.TrimStart('/')), new RouteValueDictionary()).TryMatch(bare, new RouteValueDictionary()))];
    }

    private static JsonArray Tools() =>
    [
        Schema.Function("find_api", "Finds the routes of Arena's API you may call, by words of their path (people, groups, projects, models, dashboards, audit, settings...). No words: the areas.",
            new JsonObject { ["words"] = Schema.Text("Words of the route's path, e.g. \"projects members\".") }),
        Schema.Function("read", "Reads with GET as the person signed in: their permissions, nothing more. Returns the HTTP status and the JSON.",
            new JsonObject { ["path"] = Schema.Text("The path and query, starting /api/, e.g. /api/admin/people?q=ann.") }, "path"),
        Schema.Function("propose_change", "Proposes one change (POST, PUT, PATCH or DELETE). It is NOT done: the person sees it with Yes and No, and it is done only on their yes.",
            new JsonObject
            {
                ["method"] = Schema.Choice("The method.", ["POST", "PUT", "PATCH", "DELETE"]),
                ["path"] = Schema.Text("The path, starting /api/, with real ids from what you read."),
                ["body"] = new JsonObject { ["type"] = "object", ["description"] = "The JSON body, as the route takes it (find_api shows its fields). None for DELETE." },
                ["what"] = Schema.Text("What it will do, in one plain sentence for the person, e.g. \"Adds Ann to the project Payments as a contributor.\""),
            }, "method", "path", "what"),
    ];

    private static async Task SendAsync(HttpResponse res, object e, CancellationToken ct)
    {
        await res.WriteAsync("data: " + JsonSerializer.Serialize(e, Mcp.Plain) + "\n\n", ct);
        await res.Body.FlushAsync(ct);
    }

    private static string System(AppUser me, Access.Membership m, string? page, string product) => $"""
        You are the admin assistant of {product}, an AI platform a company runs for itself (chat, models on its own GPUs, API keys,
        projects, dashboards, audit). You help {me.UserName} run it, on the admin page they have open: {(page is { Length: > 0 } ? page : "(none said)")}.
        You act only as {me.UserName}, with their permissions and no more: {string.Join(", ", Permissions.AdminKeys.Where(m.Has))}.

        Tools: find_api finds the API's routes they may call (with a change's body fields); read reads (GET) and shows the JSON;
        propose_change proposes a change, which is shown to them with Yes and No and done only on their yes.
        - Look before you answer: read what the question is about, and say what you found, with numbers and names.
        - Use real ids from what you read; never guess one. A route you may not call is not theirs: say which permission it needs.
        - One change per proposal, described plainly in "what". Never say a change is done until they said yes and you saw it succeed.
        - After a proposal, stop and wait for their answer.
        - Answer briefly in plain words, in the language they wrote in. Markdown is shown.
        """;

    private static async Task AskAsync(AssistantAsk body, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, OneQuestion one,
        GatewayChat gateway, Safeguards.Safeguards safeguards, AppPipeline pipeline, AssistantChanges changes, EndpointDataSource endpoints,
        Microsoft.Extensions.Options.IOptionsMonitor<Settings.BrandingOptions> branding, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var m = await access.MembershipAsync(me, ct);
        if (!Permissions.AdminKeys.Any(m.Has))
        {
            await Endpoints.AuthEndpoints.Problem(403, "permission", "The admin assistant is for those who hold an admin permission.").ExecuteAsync(http);
            return;
        }
        var turns = (body.Messages ?? []).Where(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Content)).TakeLast(30).ToList();
        if (turns.Count == 0 || turns[^1].Role != "user" || turns[^1].Content!.Length > 8000)
        {
            await Endpoints.AuthEndpoints.Problem(400, "messages", "End with your question, up to 8,000 characters.").ExecuteAsync(http);
            return;
        }
        var (model, problem) = await one.ModelAsync(me, body.Model, ct);
        if (problem is not null)
        {
            await problem.ExecuteAsync(http);
            return;
        }
        var verdict = await safeguards.CheckMessageAsync(me, turns[^1].Content!, 0, false, model, ct);
        if (!verdict.Allowed)
        {
            await Safeguards.Safeguards.Problem(verdict).ExecuteAsync(http);
            return;
        }
        var routes = Routes(endpoints, m);
        var product = branding.CurrentValue.ProductName is { Length: > 0 } name ? name : AppInfo.Current.Name;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = System(me, m, body.Page, product) } };
        foreach (var (t, i) in turns.Select((t, i) => (t, i)))
        {
            var text = i == turns.Count - 1 ? verdict.Text ?? t.Content! : t.Content!;
            messages.Add(new JsonObject { ["role"] = t.Role, ["content"] = text.Length > 20_000 ? text[..20_000] : text });
        }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var tools = Tools();
        for (var round = 0; round < MostRounds; round++)
        {
            var request = new JsonObject
            {
                ["model"] = verdict.Model ?? model, ["messages"] = messages.DeepClone(), ["tools"] = tools.DeepClone(), ["stream"] = true, ["max_tokens"] = 2000,
                ["user"] = me.Email, ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
                ["metadata"] = new JsonObject { ["tags"] = new JsonArray("admin-assistant") },
            };
            var said = new StringBuilder();
            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
            try
            {
                await foreach (var e in gateway.StreamAsync(request, me.Email!, ct))
                {
                    if (e is ContentDelta c)
                    {
                        said.Append(c.Text);
                    }
                    else if (e is ToolCallDelta d)
                    {
                        var call = calls.TryGetValue(d.Index, out var was) ? was : (null, null, new StringBuilder());
                        calls[d.Index] = (d.Id ?? call.Id, d.Name ?? call.Name, call.Args.Append(d.Arguments));
                    }
                }
            }
            catch (ChatGatewayException ex)
            {
                await SendAsync(http.Response, new { type = "error", message = ex.Message }, ct);
                return;
            }
            if (said.Length > 0)
            {
                var answer = await safeguards.RunAsync(new Safeguards.RuleRun("answer", said.ToString()) { User = me, Model = model }, ct);
                if (answer.Blocked)
                {
                    await SendAsync(http.Response, new { type = "error", message = answer.Reason ?? "The answer was withheld by a rule." }, ct);
                    return;
                }
                await SendAsync(http.Response, new { type = "text", text = answer.Text }, ct);
            }
            if (calls.Count == 0)
            {
                break;
            }
            var assistant = new JsonObject
            {
                ["role"] = "assistant", ["content"] = said.ToString(),
                ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Value.Id ?? $"call_{round}_{c.Key}", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Value.Name, ["arguments"] = c.Value.Args.ToString() },
                })]),
            };
            messages.Add(assistant);
            foreach (var (index, call) in calls)
            {
                var result = await CallAsync(call.Name, call.Args.ToString(), http, me, routes, pipeline, changes, ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id ?? $"call_{round}_{index}", ["content"] = result });
            }
        }
        await SendAsync(http.Response, new { type = "done" }, ct);
    }

    private static async Task<string> CallAsync(string? name, string arguments, HttpContext http, AppUser me, List<Route> routes, AppPipeline pipeline,
        AssistantChanges changes, CancellationToken ct)
    {
        JsonObject args;
        try
        {
            args = JsonNode.Parse(arguments is { Length: > 0 } ? arguments : "{}") as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return "Your arguments were not JSON.";
        }
        var path = Schema.Str(args, "path")?.Trim() ?? "";
        string? Refused() => !path.StartsWith("/api/", StringComparison.Ordinal) ? "A path starts /api/."
            : path.StartsWith(Self, StringComparison.Ordinal) ? "Not the assistant's own routes." : null;
        switch (name)
        {
            case "find_api":
                await SendAsync(http.Response, new { type = "step", tool = name, words = Schema.Str(args, "words") }, ct);
                return Find(routes, Schema.Str(args, "words"));
            case "read":
            {
                if (Refused() is { } why)
                {
                    return why;
                }
                var answer = await pipeline.SendAsync(http, "GET", path, null, ct);
                await SendAsync(http.Response, new { type = "step", tool = name, path, status = answer.Status }, ct);
                var text = answer.Body.Length > MostRead ? answer.Body[..MostRead] + $"… ({answer.Body.Length - MostRead:N0} more characters: read less, with a filter or a page)" : answer.Body;
                return $"HTTP {answer.Status}{(answer.Cut ? " (cut)" : "")}\n{text}";
            }
            case "propose_change":
            {
                var method = (Schema.Str(args, "method") ?? "").ToUpperInvariant();
                var what = Schema.Str(args, "what")?.Trim();
                if (Refused() is { } why)
                {
                    return why;
                }
                if (method is not ("POST" or "PUT" or "PATCH" or "DELETE") || what is not { Length: > 0 })
                {
                    return "A change needs its method (POST, PUT, PATCH or DELETE), its path and what it will do.";
                }
                var reached = Matching(routes, method, path);
                if (reached.Count == 0)
                {
                    return $"No route {method} {path}: find_api finds them.";
                }
                if (!reached.Any(r => r.Mine))
                {
                    return $"{me.UserName} may not: it needs {string.Join(" or ", reached.Select(r => r.Permission).OfType<string>().Distinct())}. Say so; do not propose it.";
                }
                var json = args["body"] is { } b ? (b is JsonValue v && v.TryGetValue<string>(out var s) ? s : b.ToJsonString()) : null;
                var proposal = changes.Add(me.Id, method, path, json, what.Length > 500 ? what[..500] : what);
                await SendAsync(http.Response, new { type = "proposal", id = proposal.Id, method = proposal.Method, path = proposal.Path, body = proposal.Body, what = proposal.What }, ct);
                return "Shown to the person with Yes and No. Nothing is done until they say yes: do not say it is done. End your answer here.";
            }
            default:
                return $"There is no tool {name}.";
        }
    }

    /// <summary>The person said yes: the change is made as them (their permissions, checked again now), and audited.</summary>
    private static async Task<IResult> YesAsync(Guid id, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, AssistantChanges changes, AppPipeline pipeline,
        Audit audit, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (changes.Take(id, me.Id) is not { } proposal)
        {
            return Endpoints.AuthEndpoints.Problem(404, "proposal", "That change is not waiting for you (answered already, or more than 30 minutes old).");
        }
        var answer = await pipeline.SendAsync(http, proposal.Method, proposal.Path, proposal.Body, ct);
        await audit.WriteAsync("assistant.change.made", $"{proposal.Method} {proposal.Path}", success: answer.Status < 400, detail: $"{proposal.What}; HTTP {answer.Status}");
        return Results.Ok(new { answer.Status, body = answer.Body.Length > 4000 ? answer.Body[..4000] + "…" : answer.Body });
    }

    private static async Task<IResult> NoAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AssistantChanges changes, Audit audit)
    {
        var me = (await users.GetUserAsync(p))!;
        if (changes.Take(id, me.Id) is not { } proposal)
        {
            return Endpoints.AuthEndpoints.Problem(404, "proposal", "That change is not waiting for you.");
        }
        await audit.WriteAsync("assistant.change.declined", $"{proposal.Method} {proposal.Path}", detail: proposal.What);
        return Results.NoContent();
    }
}
