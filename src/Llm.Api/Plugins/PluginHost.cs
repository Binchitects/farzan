using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Plugins;

/// <summary>
/// The host API (v1), Arena's side: what a plugin's server (through the runtime) or its frames (through the page's bridge)
/// may ask. Every call is checked here, the same way from both doors: the plugin installed and on; the scope its method
/// needs granted by an admin; for a method that acts for a person, the call's person (its token, live only while the call
/// runs), the plugin allowed to them (Admin → Tools) and the feature the method is part of theirs; and then only their
/// own things (their chats, their projects, their files, the knowledge they may read). Denials, and every call that
/// changes something or reaches out, are audited as plugin.*.
/// </summary>
public sealed class PluginHost(AppDbContext db, PluginRuntime runtime, AccessService access, Audit audit, Chat.OneQuestion one, Knowledge.KnowledgeSearch knowledge,
    Notifications.Notifier notifier, IHttpClientFactory http, IOptionsMonitor<Settings.BrandingOptions> branding, IOptions<AuthOptions> auth, TimeProvider clock,
    WebResolver resolver)
{
    public const int Denied = -32001, NotFound = -32002, RateLimited = -32003, Unavailable = -32004, InvalidParams = -32602, NoMethod = -32601;
    /// <summary>A stored value at most, its keys at most, a plugin's store at most.</summary>
    public const int MostValueBytes = 64 * 1024, MostKeyLength = 200, MostKeys = 10_000;
    public const int MostFetchBytes = 5 * 1024 * 1024;

    public sealed class HostApiException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static HostApiException Fail(int code, string message) => new(code, message);

    /// <summary>The methods whose every call is audited: those that change something or reach out of Arena.</summary>
    private static readonly HashSet<string> Audited = new(StringComparer.Ordinal)
    {
        "chats.post", "chats.create", "projects.addTask", "projects.updateTask", "models.complete", "files.put", "http.fetch", "notify.bell",
    };

    /// <summary>One host API call of the plugin's: its answer, or a <see cref="HostApiException"/> with the framework's error code.</summary>
    /// <param name="token">The call's token (from the runtime, or the bridge's); null: the plugin acts as itself.</param>
    public async Task<JsonNode?> HandleAsync(string plugin, string method, JsonObject args, string? token, CancellationToken ct)
    {
        var server = await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Plugin == plugin, ct) ?? throw Fail(NotFound, $"{plugin} is not installed.");
        var state = await db.PluginStates.AsNoTracking().SingleOrDefaultAsync(s => s.ServerId == server.Id, ct);
        if (state is { State: not PluginStates.On })
        {
            throw Fail(Unavailable, $"{plugin} is off.");
        }
        var scope = runtime.Scope(token);
        if (token is not null && (scope is null || scope.ServerId != server.Id))
        {
            await DenyAsync(plugin, method, null, "a token that is not one of its live calls");
            throw Fail(Denied, "The call this token was for has ended, or it is another plugin's.");
        }
        if (!PluginRules.Methods.TryGetValue(method, out var need))
        {
            throw Fail(NoMethod, $"The host API has no {method}.");
        }
        if (need is not null && !server.PluginPermissions.Contains(PluginRules.GrantKey(need)))
        {
            await DenyAsync(plugin, method, scope?.UserId, $"{need} is not granted");
            throw Fail(Denied, $"The plugin was not granted {need}.");
        }
        AppUser? me = null;
        Membership? member = null;
        // A dev install is its developer's alone: a call for anyone else is refused, whatever door it came by.
        if (state?.DevOwnerId is { } owner && scope?.UserId is { } someone && someone != owner)
        {
            await DenyAsync(plugin, method, someone, "a dev install of someone else's");
            throw Fail(Denied, "That is someone's dev install.");
        }
        if (scope?.UserId is { } uid)
        {
            me = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uid, ct);
            if (me is null or { IsDisabled: true })
            {
                throw Fail(Denied, "The person this call is for may not use Arena now.");
            }
            member = await access.MembershipAsync(me, ct);
            if (state?.DevOwnerId is null && !ToolRegistry.Allowed(member, McpServerTool.Prefix + server.Id))
            {
                await DenyAsync(plugin, method, uid, "the plugin is not allowed to this person");
                throw Fail(Denied, $"{server.Name} is not allowed to this person.");
            }
        }
        if (PluginRules.PersonMethods.Contains(method) && me is null)
        {
            await DenyAsync(plugin, method, null, "no person");
            throw Fail(Denied, $"{method} acts for a person, and this call is for nobody.");
        }
        var ctx = new Call(server, scope, me, member, args) { DevOwner = state?.DevOwnerId };
        var result = await DispatchAsync(method, ctx, ct);
        if (Audited.Contains(method))
        {
            await audit.WriteAsync($"plugin.call", plugin, detail: $"{method}{ctx.Detail}", actor: me);
        }
        return result;
    }

    private async Task DenyAsync(string plugin, string method, Guid? userId, string why)
    {
        var actor = userId is { } id ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id) : null;
        await audit.WriteAsync("plugin.denied", plugin, success: false, detail: $"{method}: {why}", actor: actor);
    }

    private sealed class Call(McpServer server, PluginCallScope? scope, AppUser? me, Membership? member, JsonObject args)
    {
        public McpServer Server { get; } = server;
        public PluginCallScope? Scope { get; } = scope;
        public AppUser? Me { get; } = me;
        public Membership? Member { get; } = member;
        public JsonObject Args { get; } = args;
        public string Detail { get; set; } = "";
        /// <summary>A dev install's developer: the only person it reaches.</summary>
        public Guid? DevOwner { get; init; }

        public string Str(string key) => Args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw Fail(InvalidParams, $"{key} is required.");
        public string? Opt(string key) => Args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        public Guid Id(string key = "id") => Guid.TryParse(Str(key), out var g) ? g : throw Fail(NotFound, "Not found.");
        public int Limit(int fallback, int most) => Args["limit"] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, most) : fallback;
        public bool Mine => Args["mine"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static void Needs(Call c, string feature)
    {
        if (c.Member is { } m && !m.Has(feature))
        {
            throw Fail(Denied, "The person this call is for may not do that in Arena.");
        }
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private async Task<JsonNode?> DispatchAsync(string method, Call c, CancellationToken ct)
    {
        switch (method)
        {
            case "version":
                return new JsonObject { ["arena"] = AppInfo.Current.Version, ["hostApi"] = PluginManifest.HostApiVersion };
            case "capabilities":
                return new JsonObject
                {
                    ["scopes"] = new JsonArray([.. c.Server.PluginPermissions.Where(p => p.StartsWith("scope:", StringComparison.Ordinal)).Select(p => (JsonNode)p[6..])]),
                    ["network"] = new JsonArray([.. c.Server.PluginPermissions.Where(p => p.StartsWith("host:", StringComparison.Ordinal)).Select(p => (JsonNode)p[5..])]),
                    ["hostApi"] = PluginManifest.HostApiVersion, ["arena"] = AppInfo.Current.Version,
                };

            case "chats.list":
            {
                Needs(c, Core.Access.Permissions.Chat);
                var rows = await db.Conversations.AsNoTracking().Where(x => x.UserId == c.Me!.Id && x.DeletedAt == null)
                    .OrderByDescending(x => x.UpdatedAt).Take(c.Limit(50, 200)).ToListAsync(ct);
                return new JsonArray([.. rows.Select(ChatJson)]);
            }
            case "chats.get":
                Needs(c, Core.Access.Permissions.Chat);
                return ChatJson(await MyChatAsync(c, ct));
            case "chats.messages":
            {
                Needs(c, Core.Access.Permissions.Chat);
                var chat = await MyChatAsync(c, ct);
                var rows = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == chat.Id).OrderByDescending(m => m.Sequence).Take(c.Limit(100, 500)).ToListAsync(ct);
                return new JsonArray([.. rows.OrderBy(m => m.Sequence).Select(MessageJson)]);
            }
            case "chats.create":
            {
                Needs(c, Core.Access.Permissions.Chat);
                var title = c.Str("title");
                var chat = new Conversation { UserId = c.Me!.Id, Title = title.Length > 200 ? title[..200] : title };
                db.Conversations.Add(chat);
                if (c.Opt("message") is { } text)
                {
                    db.ChatMessages.Add(new ChatMessage { ConversationId = chat.Id, Role = "user", Content = text.Length > 20000 ? text[..20000] : text, Sequence = 0 });
                }
                await db.SaveChangesAsync(ct);
                c.Detail = $" {chat.Id}";
                return ChatJson(chat);
            }
            case "chats.post":
            {
                Needs(c, Core.Access.Permissions.Chat);
                var chat = await MyChatAsync(c, ct, tracked: true);
                var content = c.Str("content");
                var last = await db.ChatMessages.Where(m => m.ConversationId == chat.Id).OrderByDescending(m => m.Sequence).FirstOrDefaultAsync(ct);
                // A note from the plugin in the chat, under its name: never a message the person wrote.
                var m = new ChatMessage
                {
                    ConversationId = chat.Id, Role = "assistant", Content = content.Length > 20000 ? content[..20000] : content,
                    Sequence = (last?.Sequence ?? -1) + 1, ParentId = last?.Id, Model = $"plugin:{c.Server.Plugin}",
                };
                db.ChatMessages.Add(m);
                chat.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                c.Detail = $" {chat.Id}";
                return MessageJson(m);
            }

            case "projects.list":
            {
                var rows = await db.Projects.AsNoTracking().Where(p => p.Status == Core.Projects.Project.Active).OrderBy(p => p.Name).Take(500).ToListAsync(ct);
                var mine = new JsonArray();
                foreach (var p in rows)
                {
                    if (await Projects.ProjectAccess.RoleAsync(db, p, c.Me!.Id, c.Member!, ct) is not null)
                    {
                        mine.Add(ProjectJson(p));
                    }
                }
                return mine;
            }
            case "projects.get":
                return ProjectJson(await MyProjectAsync(c, false, ct));
            case "projects.tasks":
            {
                var p = await MyProjectAsync(c, false, ct);
                var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == p.Id).OrderBy(t => t.CreatedAt).Take(1000).ToListAsync(ct);
                return new JsonArray([.. tasks.Select(TaskJson)]);
            }
            case "projects.addTask":
            {
                var p = await MyProjectAsync(c, true, ct);
                var title = c.Str("title");
                var t = new Core.Projects.ProjectTask { ProjectId = p.Id, Title = title.Length > 300 ? title[..300] : title };
                db.ProjectTasks.Add(t);
                await db.SaveChangesAsync(ct);
                c.Detail = $" {p.Id}";
                return TaskJson(t);
            }
            case "projects.updateTask":
            {
                var p = await MyProjectAsync(c, true, ct);
                var taskId = c.Id("taskId");
                var t = await db.ProjectTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == p.Id, ct) ?? throw Fail(NotFound, "Not found.");
                if (c.Opt("title") is { } title)
                {
                    t.Title = title.Length > 300 ? title[..300] : title;
                }
                if (c.Opt("status") is { } status)
                {
                    t.Status = status switch
                    {
                        "todo" => Core.Projects.ProjectTask.Todo, "doing" => Core.Projects.ProjectTask.Doing, "done" => Core.Projects.ProjectTask.DoneStatus,
                        _ => throw Fail(InvalidParams, "status is todo, doing or done."),
                    };
                }
                await db.SaveChangesAsync(ct);
                c.Detail = $" {p.Id}";
                return TaskJson(t);
            }

            case "models.list":
                Needs(c, Core.Access.Permissions.Chat);
                return new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "The person's default model", ["kinds"] = new JsonArray("chat") },
                    new JsonObject { ["id"] = "small", ["name"] = "The model for small steps", ["kinds"] = new JsonArray("chat") });
            case "models.complete":
            {
                Needs(c, Core.Access.Permissions.Chat);
                var prompt = c.Str("prompt");
                if (prompt.Length > 20000)
                {
                    throw Fail(InvalidParams, "A prompt is at most 20,000 characters.");
                }
                var max = c.Args["maxTokens"] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 4000) : 2000;
                var model = c.Opt("model") is { } choice && choice != "default" ? choice : null;
                var asked = await one.AskAsync(c.Me!, prompt, c.Opt("system"), model, max, "plugin:" + c.Server.Plugin, ct);
                if (asked.Problem is not null || asked.Text is null)
                {
                    throw Fail(Unavailable, "No model answered for this person now (their models, credit or safeguards).");
                }
                c.Detail = $" {asked.Model}";
                return new JsonObject { ["text"] = asked.Text, ["model"] = asked.Model, ["usage"] = null };
            }

            case "knowledge.search":
            {
                Needs(c, Core.Access.Permissions.Knowledge);
                var hits = await knowledge.SearchAsync(c.Me!, c.Str("query"), c.Limit(8, 30), ct);
                return new JsonArray([.. hits.Select(h => (JsonNode)new JsonObject
                {
                    ["title"] = h.Title, ["text"] = h.Text, ["source"] = h.Source ?? "", ["url"] = h.Url, ["score"] = h.Score,
                })]);
            }
            case "knowledge.sources":
            {
                Needs(c, Core.Access.Permissions.Knowledge);
                var sources = await db.KnowledgeSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
                return new JsonArray([.. sources.Where(s => c.Member!.MayUse(Core.Access.ResourceKinds.KnowledgeSource, s.Id.ToString()))
                    .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id.ToString(), ["name"] = s.Name, ["kind"] = s.Kind })]);
            }

            case "files.list":
            {
                var rows = await db.ChatAttachments.AsNoTracking().Where(a => a.UserId == c.Me!.Id).OrderByDescending(a => a.CreatedAt).Take(c.Limit(50, 200))
                    .Select(a => new { a.Id, a.FileName, a.Size, a.ContentType, a.CreatedAt }).ToListAsync(ct);
                return new JsonArray([.. rows.Select(a => (JsonNode)FileJson(a.Id, a.FileName, a.Size, a.ContentType, a.CreatedAt))]);
            }
            case "files.get":
            {
                var id = c.Id();
                var a = await db.ChatAttachments.AsNoTracking().Where(x => x.Id == id && x.UserId == c.Me!.Id)
                    .Select(x => new { x.Id, x.FileName, x.Size, x.ContentType, x.CreatedAt }).SingleOrDefaultAsync(ct) ?? throw Fail(NotFound, "Not found.");
                return FileJson(a.Id, a.FileName, a.Size, a.ContentType, a.CreatedAt);
            }
            case "files.content":
            {
                var id = c.Id();
                var a = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == c.Me!.Id, ct) ?? throw Fail(NotFound, "Not found.");
                var bytes = a.Data ?? Encoding.UTF8.GetBytes(a.Text);
                if (bytes.Length > MostFetchBytes)
                {
                    throw Fail(InvalidParams, "The file is larger than 5 MB.");
                }
                return new JsonObject { ["contentType"] = a.Data is null ? "text/plain; charset=utf-8" : a.ContentType, ["base64"] = Convert.ToBase64String(bytes) };
            }
            case "files.put":
            {
                Needs(c, Core.Access.Permissions.Uploads);
                var name = c.Str("name");
                var type = c.Str("contentType");
                byte[] data;
                try
                {
                    data = Convert.FromBase64String(c.Str("base64"));
                }
                catch (FormatException)
                {
                    throw Fail(InvalidParams, "base64 is not base64.");
                }
                if (data.Length > MostFetchBytes)
                {
                    throw Fail(InvalidParams, "A file is at most 5 MB.");
                }
                if (!(type.StartsWith("text/", StringComparison.Ordinal) || type is "application/json" or "text/csv" or "application/xml"))
                {
                    throw Fail(InvalidParams, "The host API v1 saves text files (text/*, JSON, XML); other kinds come in a later version.");
                }
                string text;
                try
                {
                    text = new UTF8Encoding(false, true).GetString(data);
                }
                catch (DecoderFallbackException)
                {
                    throw Fail(InvalidParams, "The file is not UTF-8 text.");
                }
                var file = new ChatAttachment { UserId = c.Me!.Id, FileName = Path.GetFileName(name), ContentType = type, Size = data.Length, Text = text, Origin = "tool" };
                db.ChatAttachments.Add(file);
                await db.SaveChangesAsync(ct);
                c.Detail = $" {file.Id}";
                return FileJson(file.Id, file.FileName, file.Size, file.ContentType, file.CreatedAt);
            }

            case "people.me":
                return await PersonJsonAsync(c.Me!, ct);
            case "people.get":
            {
                var id = c.Id();
                var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsDisabled, ct) ?? throw Fail(NotFound, "Not found.");
                return await PersonJsonAsync(u, ct);
            }
            case "people.search":
            {
                var q = "%" + c.Str("query").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                var found = await db.Users.AsNoTracking().Where(u => !u.IsDisabled && (EF.Functions.ILike(u.UserName!, q) || EF.Functions.ILike(u.DisplayName, q) || EF.Functions.ILike(u.Email!, q)))
                    .OrderBy(u => u.DisplayName).Take(c.Limit(20, 50)).ToListAsync(ct);
                var list = new JsonArray();
                foreach (var u in found)
                {
                    list.Add(await PersonJsonAsync(u, ct));
                }
                return list;
            }
            case "groups.list":
            {
                var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name, Members = db.GroupMembers.Count(m => m.GroupId == g.Id) }).ToListAsync(ct);
                return new JsonArray([.. groups.Select(g => (JsonNode)new JsonObject { ["id"] = g.Id.ToString(), ["name"] = g.Name, ["members"] = g.Members })]);
            }
            case "groups.members":
            {
                var id = c.Id();
                var people = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == id).Join(db.Users.AsNoTracking().Where(u => !u.IsDisabled), m => m.UserId, u => u.Id, (m, u) => u)
                    .OrderBy(u => u.DisplayName).Take(1000).ToListAsync(ct);
                return new JsonArray([.. people.Select(u => (JsonNode)new JsonObject { ["id"] = u.Id.ToString(), ["name"] = Name(u), ["email"] = u.Email })]);
            }

            case "settings.get":
            {
                var values = PluginInstaller.Values(c.Server, auth.Value.DataKey);
                var manifest = PluginManifest.Parse(c.Server.Manifest!);
                var o = new JsonObject();
                foreach (var s in manifest.Settings)
                {
                    // A secret is the server's (it had it at its start); a frame never sees one.
                    if (s.Type == "secret" || (values.GetValueOrDefault(s.Key) ?? s.Default) is not { } v)
                    {
                        continue;
                    }
                    o[s.Key] = v;
                }
                return o;
            }
            case "settings.company":
                return new JsonObject { ["name"] = branding.CurrentValue.ProductName, ["locale"] = "en", ["productName"] = branding.CurrentValue.ProductName, ["accent"] = null };

            case "storage.get":
            {
                var (owner, key) = StoreKey(c);
                var row = await db.PluginStore.AsNoTracking().SingleOrDefaultAsync(x => x.ServerId == c.Server.Id && x.OwnerId == owner && x.Key == key, ct);
                return row is null ? null : JsonNode.Parse(row.ValueJson);
            }
            case "storage.set":
            {
                var (owner, key) = StoreKey(c);
                var json = (c.Args["value"] ?? JsonValue.Create<string?>(null))?.ToJsonString() ?? "null";
                if (Encoding.UTF8.GetByteCount(json) > MostValueBytes)
                {
                    throw Fail(InvalidParams, $"A value is at most {MostValueBytes / 1024} KB.");
                }
                var row = await db.PluginStore.SingleOrDefaultAsync(x => x.ServerId == c.Server.Id && x.OwnerId == owner && x.Key == key, ct);
                if (row is null)
                {
                    if (await db.PluginStore.CountAsync(x => x.ServerId == c.Server.Id, ct) >= MostKeys)
                    {
                        throw Fail(RateLimited, $"The plugin keeps {MostKeys} values already, the most it may.");
                    }
                    db.PluginStore.Add(new PluginStoreEntry { ServerId = c.Server.Id, OwnerId = owner, Key = key, ValueJson = json });
                }
                else
                {
                    row.ValueJson = json;
                    row.UpdatedAt = clock.GetUtcNow();
                }
                await db.SaveChangesAsync(ct);
                return null;
            }
            case "storage.delete":
            {
                var (owner, key) = StoreKey(c);
                await db.PluginStore.Where(x => x.ServerId == c.Server.Id && x.OwnerId == owner && x.Key == key).ExecuteDeleteAsync(ct);
                return null;
            }
            case "storage.list":
            {
                var owner = c.Mine ? c.Me?.Id ?? throw Fail(Denied, "mine: true needs a person.") : Guid.Empty;
                var prefix = c.Opt("prefix") ?? "";
                var keys = await db.PluginStore.AsNoTracking().Where(x => x.ServerId == c.Server.Id && x.OwnerId == owner && x.Key.StartsWith(prefix))
                    .OrderBy(x => x.Key).Select(x => x.Key).Take(1000).ToListAsync(ct);
                return new JsonArray([.. keys.Select(k => (JsonNode)k)]);
            }

            case "http.fetch":
                return await FetchAsync(c, ct);

            case "notify.bell":
            {
                var title = c.Str("title");
                var users = c.Args["to"] is JsonArray to
                    ? to.Select(x => Guid.TryParse(x?.GetValue<string>(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList()
                    : c.Me is { } me ? [me.Id] : throw Fail(InvalidParams, "Say whom to tell (to), or call it for a person.");
                // Only people the plugin is allowed to: it cannot reach everyone's bell.
                var allowed = new List<Guid>();
                foreach (var id in users.Where(u => c.DevOwner is null || u == c.DevOwner).Take(500))
                {
                    if (await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.IsDisabled, ct) is { } u
                        && ToolRegistry.Allowed(await access.MembershipAsync(u, ct), McpServerTool.Prefix + c.Server.Id))
                    {
                        allowed.Add(id);
                    }
                }
                var link = c.Opt("link") is { } l && l.StartsWith('/') && !l.StartsWith("//", StringComparison.Ordinal) ? l : null;
                await notifier.SendManyAsync(allowed, new Notifications.News("plugin", $"{c.Server.Name}: {Cut(title, 200)}", c.Opt("body") is { } b ? Cut(b, 2000) : null, link), ct);
                c.Detail = $" to {allowed.Count}";
                return null;
            }
            case "audit.write":
            {
                var action = c.Str("action");
                if (!System.Text.RegularExpressions.Regex.IsMatch(action, "^[a-z0-9_.-]{1,60}$"))
                {
                    throw Fail(InvalidParams, "action is lowercase letters, digits, dots, dashes and underscores, up to 60.");
                }
                await audit.WriteAsync($"plugin.{c.Server.Plugin}.{action}", c.Opt("target") is { } t ? Cut(t, 200) : null,
                    detail: c.Opt("detail") is { } d ? Cut(d, 1000) : null, actor: c.Me);
                return null;
            }
        }
        throw Fail(NoMethod, $"The host API has no {method}.");
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..most] : s;

    private static (Guid Owner, string Key) StoreKey(Call c)
    {
        var key = c.Str("key");
        if (key.Length > MostKeyLength)
        {
            throw Fail(InvalidParams, $"A key is at most {MostKeyLength} characters.");
        }
        return (c.Mine ? c.Me?.Id ?? throw Fail(Denied, "mine: true needs a person.") : Guid.Empty, key);
    }

    private async Task<Conversation> MyChatAsync(Call c, CancellationToken ct, bool tracked = false)
    {
        var id = c.Id();
        var q = tracked ? db.Conversations : db.Conversations.AsNoTracking();
        return await q.SingleOrDefaultAsync(x => x.Id == id && x.UserId == c.Me!.Id && x.DeletedAt == null, ct) ?? throw Fail(NotFound, "Not found.");
    }

    private async Task<Core.Projects.Project> MyProjectAsync(Call c, bool write, CancellationToken ct)
    {
        var id = c.Id();
        var p = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Fail(NotFound, "Not found.");
        var role = await Projects.ProjectAccess.RoleAsync(db, p, c.Me!.Id, c.Member!, ct) ?? throw Fail(NotFound, "Not found.");
        if (write && role == Core.Projects.ProjectMember.Viewer)
        {
            throw Fail(Denied, "The person this call is for may only look at that project.");
        }
        return p;
    }

    private static JsonNode ChatJson(Conversation x) => new JsonObject
    {
        ["id"] = x.Id.ToString(), ["title"] = x.Title, ["createdAt"] = Iso(x.CreatedAt), ["updatedAt"] = Iso(x.UpdatedAt), ["projectId"] = x.WorkProjectId?.ToString(),
    };

    private static JsonNode MessageJson(ChatMessage m) => new JsonObject
    {
        ["id"] = m.Id.ToString(), ["role"] = m.Role, ["content"] = m.Content, ["createdAt"] = Iso(m.CreatedAt), ["model"] = m.Model,
    };

    private static JsonObject ProjectJson(Core.Projects.Project p) => new JsonObject
    {
        ["id"] = p.Id.ToString(), ["name"] = p.Name, ["description"] = p.Description.Length > 0 ? p.Description : null, ["createdAt"] = Iso(p.CreatedAt),
    };

    private static JsonNode TaskJson(Core.Projects.ProjectTask t) => new JsonObject
    {
        ["id"] = t.Id.ToString(), ["projectId"] = t.ProjectId.ToString(), ["title"] = t.Title,
        ["status"] = t.Status switch { Core.Projects.ProjectTask.DoneStatus => "done", Core.Projects.ProjectTask.Doing => "doing", _ => "todo" },
    };

    private static JsonObject FileJson(Guid id, string name, long size, string type, DateTimeOffset at) => new()
    {
        ["id"] = id.ToString(), ["name"] = name, ["size"] = size, ["contentType"] = type, ["createdAt"] = Iso(at),
    };

    private static string Name(AppUser u) => u.DisplayName.Length > 0 ? u.DisplayName : u.UserName ?? "";

    private async Task<JsonNode> PersonJsonAsync(AppUser u, CancellationToken ct)
    {
        var groups = await db.GroupMembers.AsNoTracking().Where(m => m.UserId == u.Id).Join(db.Groups.AsNoTracking(), m => m.GroupId, g => g.Id, (m, g) => g.Name).ToListAsync(ct);
        return new JsonObject { ["id"] = u.Id.ToString(), ["name"] = Name(u), ["email"] = u.Email, ["groups"] = new JsonArray([.. groups.Select(g => (JsonNode)g)]) };
    }

    /// <summary>
    /// A request to the internet for the plugin, made here: only to the hosts granted it (by name), https unless its
    /// grant says http, only to public addresses (the web tool's guard: no Arena, no office network, no cloud metadata,
    /// a name that turns private refused at the connection), each redirect checked the same, the answer at most 5 MB.
    /// </summary>
    private async Task<JsonNode> FetchAsync(Call c, CancellationToken ct)
    {
        if (!Uri.TryCreate(c.Str("url"), UriKind.Absolute, out var url))
        {
            throw Fail(InvalidParams, "url is not an address.");
        }
        var method = (c.Opt("method") ?? "GET").ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
        {
            throw Fail(InvalidParams, $"{method} is not a method http.fetch makes.");
        }
        var client = http.CreateClient(WebFetcher.Client);
        client.Timeout = TimeSpan.FromSeconds(30);
        for (var hop = 0; hop <= 5; hop++)
        {
            var name = url.Host.ToLowerInvariant();
            var host = url.IsDefaultPort ? name : $"{name}:{url.Port}";
            var grants = c.Server.PluginPermissions.Where(p => p.StartsWith("host:", StringComparison.Ordinal)).Select(p => p[5..]).ToList();
            bool Matches(string g) => g == host || g == name || (g.StartsWith("*.", StringComparison.Ordinal) && name.EndsWith(g[1..], StringComparison.Ordinal));
            if (!grants.Any(Matches))
            {
                await DenyAsync(c.Server.Plugin!, "http.fetch", c.Me?.Id, $"{url.Host} is not granted");
                throw Fail(Denied, $"{url.Host} is not among the hosts this plugin was granted.");
            }
            if (url.Scheme != Uri.UriSchemeHttps)
            {
                throw Fail(Denied, "Only https addresses are reached.");
            }
            // Where the name points, before anything is sent: only the public internet (never Arena, the office, cloud metadata). The
            // connection checks it again as it is made, so a name that turns private meanwhile is refused there.
            System.Net.IPAddress[] addresses;
            try
            {
                addresses = await resolver.ResolveAsync(url.Host, ct);
            }
            catch (System.Net.Sockets.SocketException)
            {
                throw Fail(Unavailable, $"{url.Host} could not be found.");
            }
            if (addresses.Length == 0 || addresses.Any(a => !WebGuard.IsPublic(a)))
            {
                await DenyAsync(c.Server.Plugin!, "http.fetch", c.Me?.Id, $"{url.Host} is not on the public internet");
                throw Fail(Unavailable, $"{url.Host} is not on the public internet.");
            }
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (c.Args["headers"] is JsonObject headers)
            {
                foreach (var (k, v) in headers)
                {
                    if (v?.GetValue<string>() is { } value && k.ToLowerInvariant() is not ("host" or "cookie" or "content-length" or "transfer-encoding" or "connection" or "proxy-authorization"))
                    {
                        if (!request.Headers.TryAddWithoutValidation(k, value))
                        {
                            request.Content ??= new ByteArrayContent([]);
                            request.Content.Headers.TryAddWithoutValidation(k, value);
                        }
                    }
                }
            }
            if (c.Opt("body") is { } body && method is not ("GET" or "HEAD"))
            {
                var type = request.Content?.Headers.ContentType?.ToString();
                request.Content = new StringContent(body, Encoding.UTF8);
                if (type is not null)
                {
                    request.Content.Headers.Remove("Content-Type");
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                }
            }
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or WebAccessException)
            {
                throw Fail(Unavailable, $"{url.Host} could not be reached: {(ex is TaskCanceledException ? "it took longer than 30 s" : ex.Message)}");
            }
            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    url = new Uri(url, next);
                    method = response.StatusCode is System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect ? method : "GET";
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(chunk, ct)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MostFetchBytes)
                    {
                        throw Fail(InvalidParams, $"{url.Host}'s answer is larger than 5 MB.");
                    }
                }
                var bytes = buffer.ToArray();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
                var text = contentType.Length == 0 || contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.Contains("json", StringComparison.Ordinal) || contentType.Contains("xml", StringComparison.Ordinal);
                var outHeaders = new JsonObject();
                foreach (var h in response.Headers.Concat(response.Content.Headers).Where(h => !string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)))
                {
                    outHeaders[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                }
                c.Detail = $" {method} {url.Host}{url.AbsolutePath} {(int)response.StatusCode}";
                return new JsonObject
                {
                    ["status"] = (int)response.StatusCode, ["headers"] = outHeaders,
                    ["body"] = text ? Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes), ["base64"] = !text,
                };
            }
        }
        throw Fail(Unavailable, "More than 5 redirects.");
    }
}
