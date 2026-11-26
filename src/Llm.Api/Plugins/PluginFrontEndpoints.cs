using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Plugins;

public sealed record PluginToolRequest(JsonObject? Arguments, bool Confirmed = false, Guid? ChatId = null, Guid? ProjectId = null);

public sealed record PluginHostRequest(string Method, JsonObject? Params, Guid? ChatId = null, Guid? ProjectId = null);

/// <summary>
/// Plugins as people meet them: the plugins on for them and what each adds to the page (GET /api/plugins/mine); each
/// plugin's UI files, for its sandboxed frames (/api/plugin-ui/{plugin}/{version}/{key}/...); its routes
/// (/api/p/{plugin}/...); and the page's bridge for its frames: their host API calls and their calls of the plugin's
/// tools. Every call is the person's, checked as theirs; non-GET calls carry the bridge's header (X-Arena-Bridge), which a
/// page of another site cannot send. And POST /api/plugins/packages: the CLI's publish and dev, with a person's API key.
/// </summary>
public static class PluginFrontEndpoints
{
    public const string BridgeHeader = "X-Arena-Bridge";
    /// <summary>The frames' policy: an opaque origin (no cookies, no storage, no Arena), no network of their own, framed only by Arena.</summary>
    /// <remarks>No allow-popups (window.open would carry what it read to any site), no top navigation; a frame that navigates itself
    /// off Arena is refused by the page's own policy (frame-src 'self').</remarks>
    public const string FrameCsp = "sandbox allow-scripts allow-forms allow-modals; default-src 'none'; script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; media-src 'self' data: blob:; connect-src 'none'; " +
        "frame-src 'none'; worker-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
    /// <summary>A route's answer: data, never a page in Arena's origin.</summary>
    public const string RouteCsp = "sandbox; default-src 'none'; frame-ancestors 'none'";
    public const long MostRouteBody = 1024 * 1024;

    public static void MapPluginFront(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/plugins").PersonOnly();
        me.MapGet("/mine", MineAsync);
        me.MapPost("/{plugin}/tools/{function}", ToolAsync);
        me.MapPost("/{plugin}/host", HostAsync);
        app.MapPost("/api/plugins/packages", PublishAsync).AllowAnonymous("The CLI's publish and dev: the person's API key is checked on each call, and their permission.");
        app.MapGet("/api/plugin-ui/{plugin}/{version}/{key}/{**path}", UiFileAsync)
            .AllowAnonymous("A plugin's UI files for its sandboxed frames: the path's key, made from the app's key for that version, is given only to people the plugin is on for.");
        app.Map("/api/p/{plugin}/{**rest}", RouteAsync).PersonOnly();
    }

    /// <summary>The key in a plugin's UI address: unguessable, the same for everyone the plugin is on for, another for each version.</summary>
    public static string UiKey(Guid serverId, string version, string? appKey) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("plugin-ui:" + (appKey ?? "")), Encoding.UTF8.GetBytes($"{serverId}:{version}")))[..32];

    /// <summary>A plugin installed and on for this person (and, for a dev install, theirs), with its manifest; or null.</summary>
    internal static async Task<(McpServer Server, PluginManifest Manifest, PluginState? State)?> OnForAsync(AppDbContext db, AccessService access, AppUser me, string plugin, CancellationToken ct)
    {
        if (await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Plugin == plugin, ct) is not { Manifest: { } text } server)
        {
            return null;
        }
        var state = await db.PluginStates.AsNoTracking().SingleOrDefaultAsync(s => s.ServerId == server.Id, ct);
        if (state is { State: not PluginStates.On } || (state?.DevOwnerId is { } owner && owner != me.Id))
        {
            return null;
        }
        if (state?.DevOwnerId is not null)
        {
            // Its developer's own: off in everyone's chat (its tool row), on for them here.
            return (server, PluginManifest.Parse(text), state);
        }
        var setting = await db.ToolSettings.AsNoTracking().SingleOrDefaultAsync(t => t.ToolId == McpServerTool.Prefix + server.Id, ct);
        if (setting is { Enabled: false } || !ToolRegistry.Allowed(await access.MembershipAsync(me, ct), McpServerTool.Prefix + server.Id))
        {
            return null;
        }
        return (server, PluginManifest.Parse(text), state);
    }

    /// <summary>Whether the person may manage this plugin: its role says so, or they are among its managers (themselves or a group).</summary>
    internal static async Task<bool> ManagesAsync(AppDbContext db, AccessService access, AppUser me, Guid serverId, CancellationToken ct)
    {
        if (await access.MayAsync(me, Core.Access.Permissions.PluginsConfigure, ct))
        {
            return true;
        }
        var groups = await db.GroupMembers.AsNoTracking().Where(g => g.UserId == me.Id).Select(g => g.GroupId).ToListAsync(ct);
        return await db.PluginManagers.AsNoTracking().AnyAsync(m => m.ServerId == serverId && (m.UserId == me.Id || (m.GroupId != null && groups.Contains(m.GroupId.Value))), ct);
    }

    private static async Task<IResult> MineAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var servers = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null && s.Url.StartsWith(PluginManifest.PluginScheme)).OrderBy(s => s.Name).ToListAsync(ct);
        var list = new JsonArray();
        foreach (var s in servers)
        {
            if (await OnForAsync(db, access, me, s.Plugin!, ct) is not var (server, m, state) || !m.Ui.Any)
            {
                continue;
            }
            var manages = await ManagesAsync(db, access, me, server.Id, ct);
            var ui = m.Ui;
            JsonArray Arr<T>(IEnumerable<T> xs, Func<T, JsonNode> f) => new([.. xs.Select(f)]);
            list.Add(new JsonObject
            {
                ["name"] = m.Name, ["title"] = server.Name, ["version"] = m.Version, ["icon"] = m.Icon, ["dev"] = state?.DevOwnerId is not null, ["manages"] = manages,
                ["base"] = $"/api/plugin-ui/{m.Name}/{m.Version}/{UiKey(server.Id, m.Version, auth.Value.DataKey)}/",
                ["pages"] = Arr(ui.Pages, x => new JsonObject { ["id"] = x.Id, ["title"] = x.Title, ["path"] = x.Path, ["entry"] = x.Entry, ["icon"] = x.Icon }),
                ["menu"] = Arr(ui.Menu, x => new JsonObject { ["page"] = x.Page, ["section"] = x.Section, ["title"] = x.Title }),
                ["panels"] = Arr(ui.Panels, x => new JsonObject { ["id"] = x.Id, ["title"] = x.Title, ["slot"] = x.Slot, ["entry"] = x.Entry }),
                ["settings"] = manages ? ui.Settings : null,
                ["composerActions"] = Arr(ui.ComposerActions, x => new JsonObject { ["id"] = x.Id, ["title"] = x.Title, ["entry"] = x.Entry, ["run"] = x.Run }),
                ["messageActions"] = Arr(ui.MessageActions, x => new JsonObject { ["id"] = x.Id, ["title"] = x.Title, ["entry"] = x.Entry, ["run"] = x.Run }),
                ["idePanels"] = Arr(ui.IdePanels, x => new JsonObject { ["id"] = x.Id, ["title"] = x.Title, ["entry"] = x.Entry }),
                ["writes"] = Arr(m.Functions.Where(f => f.Writes), f => f.Name),
            });
        }
        return Results.Ok(list);
    }

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json", [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8", [".map"] = "application/json",
    };

    /// <summary>
    /// One of a plugin's UI files, from the package of the version installed, under the frames' policy: a document served
    /// here has an opaque origin (CSP sandbox) even when opened on its own, so it never reads Arena's cookies, storage or API.
    /// </summary>
    private static async Task<IResult> UiFileAsync(string plugin, string version, string key, string path, HttpContext http, AppDbContext db, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Plugin == plugin, ct) is not { } server
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(UiKey(server.Id, version, auth.Value.DataKey)), Encoding.UTF8.GetBytes(key)))
        {
            return Results.NotFound();
        }
        var release = await db.PluginReleases.AsNoTracking().Where(r => r.ServerId == server.Id && r.Version == version).OrderByDescending(r => r.InstalledAt)
            .Select(r => r.Id).FirstOrDefaultAsync(ct);
        var blob = await db.PluginPackageBlobs.AsNoTracking().SingleOrDefaultAsync(b => b.ReleaseId == release, ct);
        if (blob is null || !PluginFap.SafePath(path))
        {
            return Results.NotFound();
        }
        var (files, _) = PluginFap.Read(blob.Bytes);
        if (!files.TryGetValue(path, out var data) || path.StartsWith("server/", StringComparison.Ordinal) || path == PluginManifest.FileName)
        {
            return Results.NotFound();
        }
        var h = http.Response.Headers;
        h.ContentSecurityPolicy = FrameCsp;
        h.XFrameOptions = "SAMEORIGIN";
        h.CacheControl = "private, max-age=3600";
        // Its own frame has an opaque origin, never the same as Arena's: same-origin would block the frame's own scripts. The key in
        // the address keeps other sites out, and nothing here is a secret (the plugin's UI code).
        h["Cross-Origin-Resource-Policy"] = "cross-origin";
        return Results.Bytes(data, Types.GetValueOrDefault(Path.GetExtension(path)) ?? "application/octet-stream");
    }

    private static bool FromBridge(HttpContext http) => http.Request.Headers[BridgeHeader] == "1";

    /// <summary>A route of the plugin's, as the person: its manifest says it and who may call it; its answer is data (JSON or text), never a page.</summary>
    private static async Task<IResult> RouteAsync(string plugin, string? rest, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        AccessService access, PluginCalls calls, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var method = http.Request.Method.ToUpperInvariant();
        if (!HttpMethods.IsGet(method) && !FromBridge(http))
        {
            return AuthEndpoints.Problem(403, "bridge", $"A plugin's routes are called from Arena's pages (the {BridgeHeader} header).");
        }
        if (await OnForAsync(db, access, me, plugin, ct) is not var (server, m, state))
        {
            return Results.NotFound();
        }
        var path = "/" + (rest ?? "");
        if (path.Split('/').Any(x => x is "." or ".."))
        {
            return Results.NotFound();
        }
        var route = m.Routes.FirstOrDefault(r => r.Method == method && Match(r.Path, path) is not null);
        if (route is null)
        {
            return Results.NotFound();
        }
        if (route.Access == "manager" && !await ManagesAsync(db, access, me, server.Id, ct))
        {
            return Results.Forbid();
        }
        string? body = null;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsDelete(method))
        {
            if (http.Request.ContentLength > MostRouteBody)
            {
                return AuthEndpoints.Problem(413, "body", "A plugin's route takes at most 1 MB.");
            }
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var buffer = new char[MostRouteBody + 1];
            var n = await reader.ReadBlockAsync(buffer, ct);
            if (n > MostRouteBody)
            {
                return AuthEndpoints.Problem(413, "body", "A plugin's route takes at most 1 MB.");
            }
            body = new string(buffer, 0, n);
        }
        var query = new JsonObject();
        foreach (var (k, v) in http.Request.Query)
        {
            query[k] = v.ToString();
        }
        var headers = new JsonObject();
        foreach (var name in new[] { "accept", "content-type", "accept-language" })
        {
            if (http.Request.Headers.TryGetValue(name, out var v))
            {
                headers[name] = v.ToString();
            }
        }
        var who = new PluginCallScope(server.Id, server.Plugin!, me.Id, null, null, DateTimeOffset.MaxValue, state?.DevOwnerId is not null);
        var r = await calls.CallAsync(server, m, "route.handle", new JsonObject
        {
            ["method"] = method, ["route"] = route.Path, ["path"] = path, ["params"] = Match(route.Path, path), ["query"] = query, ["headers"] = headers, ["body"] = body,
        }, who, ct);
        http.Response.Headers.ContentSecurityPolicy = RouteCsp;
        if (!r.Ok)
        {
            return AuthEndpoints.Problem(r.Kind == "off" ? 503 : 502, "plugin", $"{server.Name}: {r.Message}");
        }
        var status = r.Result?["status"]?.GetValue<int>() ?? 200;
        var text = r.Result?["body"]?.GetValue<string>() ?? "";
        var type = r.Result?["headers"]?["content-type"]?.GetValue<string>() ?? "application/json";
        // Data only: JSON, or plain text; anything else (a page, a script) is sent as text, never run in Arena's origin.
        type = type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/plain; charset=utf-8";
        return Results.Text(text, type, Encoding.UTF8, status is >= 200 and < 600 ? status : 502);
    }

    /// <summary>The route's parameters for a path ({id} in /tickets/{id}), or null when it does not match.</summary>
    public static JsonObject? Match(string route, string path)
    {
        var a = route.Split('/');
        var b = path.Split('?')[0].Split('/');
        if (a.Length != b.Length)
        {
            return null;
        }
        var o = new JsonObject();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].Length > 2 && a[i][0] == '{' && a[i][^1] == '}')
            {
                o[a[i][1..^1]] = Uri.UnescapeDataString(b[i]);
            }
            else if (a[i] != b[i])
            {
                return null;
            }
        }
        return o;
    }

    /// <summary>One of the plugin's tools, called from its frame (or a message action) as the person: one that writes needs their yes (the page asks).</summary>
    private static async Task<IResult> ToolAsync(string plugin, string function, PluginToolRequest body, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users,
        AppDbContext db, AccessService access, PluginCalls calls, CancellationToken ct)
    {
        if (!FromBridge(http))
        {
            return AuthEndpoints.Problem(403, "bridge", $"A plugin's tools are called from Arena's pages (the {BridgeHeader} header).");
        }
        var me = (await users.GetUserAsync(p))!;
        if (await OnForAsync(db, access, me, plugin, ct) is not var (server, m, state) || m.Functions.FirstOrDefault(f => f.Name == function) is not { } fn)
        {
            return Results.NotFound();
        }
        if (fn.Writes && !body.Confirmed)
        {
            return AuthEndpoints.Problem(409, "confirm", $"{fn.Name} changes something: the person says yes first.");
        }
        var chat = body.ChatId is { } c && await db.Conversations.AsNoTracking().AnyAsync(x => x.Id == c && x.UserId == me.Id, ct) ? body.ChatId : null;
        var who = new PluginCallScope(server.Id, server.Plugin!, me.Id, chat, body.ProjectId, DateTimeOffset.MaxValue, state?.DevOwnerId is not null);
        var r = await calls.CallAsync(server, m, "tool.call", new JsonObject { ["name"] = fn.Name, ["arguments"] = body.Arguments?.DeepClone() ?? new JsonObject() }, who, ct);
        return r.Ok
            ? Results.Ok(new { content = r.Result?["content"]?.GetValue<string>() ?? "", isError = r.Result?["isError"]?.GetValue<bool>() == true })
            : Results.Ok(new { content = $"{server.Name} failed: {r.Message}", isError = true });
    }

    /// <summary>A frame's host API call, relayed by the page: as the person looking at it, checked as the plugin's server's are.</summary>
    private static async Task<IResult> HostAsync(string plugin, PluginHostRequest body, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db,
        AccessService access, PluginRuntime runtime, PluginHost host, CancellationToken ct)
    {
        if (!FromBridge(http))
        {
            return AuthEndpoints.Problem(403, "bridge", $"A plugin's frames reach Arena through its pages (the {BridgeHeader} header).");
        }
        var me = (await users.GetUserAsync(p))!;
        if (await OnForAsync(db, access, me, plugin, ct) is not var (server, _, state))
        {
            return Results.NotFound();
        }
        var token = runtime.Mint(new PluginCallScope(server.Id, plugin, me.Id, body.ChatId, body.ProjectId, DateTimeOffset.UtcNow.AddSeconds(60), state?.DevOwnerId is not null));
        try
        {
            var result = await host.HandleAsync(plugin, body.Method, body.Params ?? [], token, ct);
            return Results.Ok(new { result });
        }
        catch (PluginHost.HostApiException ex)
        {
            return Results.Ok(new { error = new { code = ex.Code, message = ex.Message } });
        }
        finally
        {
            runtime.Revoke(token);
        }
    }

    /// <summary>
    /// The CLI's publish (and dev, with ?dev=true): a .fap posted with a person's API key. A new plugin installs with only
    /// what needs nobody's yes granted, off until an admin grants the rest and turns it on; a newer version of one installed
    /// updates it when it asks for nothing more. A dev install is the developer's own, under its own name, seen only by them.
    /// </summary>
    private static async Task<IResult> PublishAsync(HttpContext http, ArenaMcp.McpPeople people, AppDbContext db, AccessService access, Audit audit, IOptions<AuthOptions> auth,
        IOptionsMonitor<PluginOptions> options, PluginRuntime runtime, CancellationToken ct)
    {
        var (me, refusal) = await Chat.CodeArenaSync.CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        var dev = http.Request.Query["dev"] == "true";
        if (dev ? !options.CurrentValue.DevInstalls || !await access.MayAsync(me, Core.Access.Permissions.PluginsDevelop, ct) : !await access.MayAsync(me, Core.Access.Permissions.PluginsInstall, ct))
        {
            return AuthEndpoints.Problem(403, "permission", dev ? "Dev installs are off here, or not yours to make (Develop plugins)." : "Installing plugins is not yours to do here (Install plugins).");
        }
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length is 0 or > PluginCatalog.MaxZipBytes)
        {
            return AuthEndpoints.Problem(400, "package", "Post the .fap as the body, at most 10 MB.");
        }
        PluginPackage package;
        try
        {
            package = await PluginContent.TrustAsync(db, PluginCatalog.FromFap(buffer.ToArray()), ct);
        }
        catch (Exception ex) when (ex is PluginCatalog.PluginException or PluginManifest.ManifestException)
        {
            return AuthEndpoints.Problem(400, ex is PluginCatalog.PluginException pe ? pe.Code : ((PluginManifest.ManifestException)ex).Code, ex.Message);
        }
        if (PluginContent.Refusal(package, options.CurrentValue.AllowUnsigned || dev) is { } refused)
        {
            return AuthEndpoints.Problem(400, "package", refused);
        }
        var name = package.Manifest.Name;
        if (dev)
        {
            // Its own name, so it never touches the company's install of the same plugin.
            var devName = $"{name}--dev-{me.Id.ToString("N")[..6]}";
            if (devName.Length > 50)
            {
                return AuthEndpoints.Problem(400, "name", "The plugin's name is too long for a dev install (at most 37 characters).");
            }
            // Each push a version of its own (0.1.0-dev.20261126.093012), so its worker and frames take the new code at once.
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd.HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var core = package.Manifest.Version.Split('-', '+')[0];
            var text = System.Text.RegularExpressions.Regex.Replace(package.ManifestText, @"(^|\n)name:[ \t]*[^\n]+", $"$1name: {devName}");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(^|\n)version:[ \t]*[^\n]+", $"$1version: {core}-dev.{stamp}");
            package = new PluginPackage(PluginManifest.Parse(text), text, package.Spec)
            {
                Files = package.Files, SandboxFiles = package.SandboxFiles, AllFiles = package.AllFiles, Signature = package.Signature, Bytes = package.Bytes,
            };
            name = devName;
        }
        var permissions = PluginContent.Permissions(package);
        var server = await db.McpServers.SingleOrDefaultAsync(s => s.Plugin == name, ct);
        if (server is null)
        {
            server = new McpServer { Name = "", Url = "" };
            if (PluginInstaller.Apply(server, package, new Dictionary<string, string?>(), auth.Value.DataKey) is { } problem)
            {
                return AuthEndpoints.Problem(400, "settings", $"{problem} An admin fills its settings in Admin → Plugins.");
            }
            // A dev install has what it asks for, within the developer's own access (checked on every call); others wait for an admin.
            var yes = PluginContent.NeedingYes(permissions).ToHashSet();
            server.PluginPermissions = [.. permissions.Where(x => dev || !yes.Contains(x.Key)).Select(x => x.Key)];
            if (dev)
            {
                server.Name = $"{package.Manifest.Title} (dev, {me.UserName})";
            }
            db.McpServers.Add(server);
            await db.SaveChangesAsync(ct);
            if (dev)
            {
                // Never in anyone's chat or at Arena MCP, its developer's neither: they try it through its pages and the CLI.
                db.ToolSettings.Add(new ToolSetting { ToolId = McpServerTool.Prefix + server.Id, Enabled = false });
            }
            var release = PluginContent.Release(server, package, me.Id);
            db.PluginReleases.Add(release);
            PluginContent.Keep(db, release, package);
            await PluginContent.StateAsync(db, server, package, dev ? me.Id : null, ct);
            var waiting = permissions.Where(x => !server.PluginPermissions.Contains(x.Key)).Select(x => x.Key).ToList();
            if (waiting.Count > 0)
            {
                var state = await db.PluginStates.SingleOrDefaultAsync(s => s.ServerId == server.Id, ct) ?? db.PluginStates.Add(new PluginState { ServerId = server.Id }).Entity;
                state.State = PluginStates.Off;
                state.Reason = $"Published by {me.UserName}: waiting for an admin to grant {string.Join(", ", waiting)}.";
            }
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(dev ? "plugin.dev_install" : "plugin.publish", name, detail: $"{package.Manifest.Version} by API key; granted {string.Join(", ", server.PluginPermissions)}", actor: me);
            return Results.Ok(new { id = server.Id, name, version = package.Manifest.Version, state = waiting.Count > 0 ? "installed, off until an admin grants what it asks for" : "installed",
                waiting, url = package.Manifest.Ui.Pages.Length > 0 ? $"/p/{name}/{package.Manifest.Ui.Pages[0].Path}" : null });
        }
        if (dev && await db.PluginStates.AsNoTracking().AnyAsync(s => s.ServerId == server.Id && s.DevOwnerId != me.Id, ct))
        {
            return AuthEndpoints.Problem(403, "dev", "That dev install is someone else's.");
        }
        var more = permissions.Where(x => !server.PluginPermissions.Contains(x.Key)).ToList();
        if (!dev && PluginContent.NeedingYes(more).Any())
        {
            return Results.Json(new { status = "accept", error = $"{package.Manifest.Title} {package.Manifest.Version} asks for more than was granted: an admin updates it in Admin → Plugins.",
                waiting = more.Select(x => x.Key) }, statusCode: 409);
        }
        if (PluginInstaller.Apply(server, package, new Dictionary<string, string?>(), auth.Value.DataKey) is { } why)
        {
            return AuthEndpoints.Problem(400, "settings", why);
        }
        if (dev)
        {
            server.PluginPermissions = [.. permissions.Select(x => x.Key)];
        }
        var kept = PluginContent.Release(server, package, me.Id);
        db.PluginReleases.Add(kept);
        PluginContent.Keep(db, kept, package);
        await PluginContent.StateAsync(db, server, package, dev ? me.Id : null, ct);
        await db.SaveChangesAsync(ct);
        await runtime.StopAsync(name, ct);
        await audit.WriteAsync(dev ? "plugin.dev_update" : "plugin.publish", name, detail: $"{package.Manifest.Version} by API key", actor: me);
        return Results.Ok(new { id = server.Id, name, version = package.Manifest.Version, state = "updated", waiting = Array.Empty<string>(),
            url = package.Manifest.Ui.Pages.Length > 0 ? $"/p/{name}/{package.Manifest.Ui.Pages[0].Path}" : null });
    }
}
