using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Llm.Api.Plugins.PluginManifest;

namespace Llm.Api.Plugins;

/// <summary>Reads a manifest's parsed document by the framework's rules; the first rule broken is the exception, with its code.</summary>
internal sealed partial class ManifestReader(JsonObject doc)
{
    private static readonly string[] ToolSources = ["openapi", "mcp", "sandbox", "functions"];

    private static ManifestException Fail(string code, string message) => new(code, message);

    private static string? Str(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<string>(out var s) ? (s.Trim().Length > 0 ? s.Trim() : null) : v.ToJsonString().Trim('"')
        : null;

    private static JsonArray List(JsonNode? n, string path) => n switch
    {
        null => [],
        JsonArray a => a,
        _ => throw Fail("manifest.type", $"{path} must be a list."),
    };

    private static string[] Strings(JsonNode? n, string path) => [.. List(n, path).Select((x, i) => Str(x) ?? throw Fail("manifest.type", $"{path}[{i}] must be text."))];

    private static JsonObject Map(JsonNode? n, string path) => n as JsonObject ?? throw Fail("manifest.type", $"{path} must be a mapping.");

    private static int Int(JsonNode? n, string path, int fallback, int min, int max)
    {
        if (n is null)
        {
            return fallback;
        }
        if (Str(n) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max)
        {
            return v;
        }
        throw Fail("manifest.range", $"{path} must be a whole number from {min} to {max}.");
    }

    private static bool Bool(JsonNode? n, string path) => n is null ? false : Str(n) switch
    {
        "true" => true,
        "false" => false,
        _ => throw Fail("manifest.type", $"{path} must be true or false."),
    };

    /// <summary>A path inside the plugin: relative, no "..", no backslashes, no drive.</summary>
    public static bool Inside(string p) => p.Length > 0 && !p.StartsWith('/') && !p.Contains('\\') && !p.Contains('\0') && !Regex.IsMatch(p, "^[A-Za-z]:")
        && p.Split('/').All(x => x is not ("" or ".."));

    private static string File(JsonNode? n, string path)
    {
        var s = Str(n) ?? throw Fail("manifest.required", $"{path} is required.");
        s = s.TrimEnd('/');
        return Inside(s) ? s : throw Fail("manifest.path.outside", $"{path} ({s}) must be a path inside the plugin.");
    }

    private static string Html(JsonNode? n, string path)
    {
        var f = File(n, path);
        return f.EndsWith(".html", StringComparison.Ordinal) || f.EndsWith(".htm", StringComparison.Ordinal) ? f : throw Fail("manifest.ui.entry", $"{path} must be an .html file.");
    }

    public PluginManifest Read()
    {
        var apiVersion = Str(doc["apiVersion"]);
        if (apiVersion is null)
        {
            throw Fail("manifest.api_version.missing", $"The manifest has no apiVersion. A plugins v2 manifest becomes v3 with `arena-plugin migrate` (apiVersion: {ApiVersion}).");
        }
        if (apiVersion != ApiVersion)
        {
            throw Fail("manifest.api_version.unsupported", $"apiVersion {apiVersion} is not {ApiVersion}.");
        }
        var name = Str(doc["name"]) ?? throw Fail("manifest.name.missing", "The manifest has no name.");
        if (!PluginName().IsMatch(name))
        {
            throw Fail("manifest.name.invalid", $"\"{name}\" is not a plugin name: lowercase letters, digits and dashes, up to 50.");
        }
        var version = Str(doc["version"]) ?? throw Fail("manifest.version.missing", "The manifest has no version.");
        if (!IsVersion(version))
        {
            throw Fail("manifest.version.invalid", $"\"{version}\" is not a version like 1.2.3.");
        }
        var arena = Str(doc["arena"]) ?? ">=8.3.0";
        if (!PluginRules.IsRange(arena))
        {
            throw Fail("manifest.arena.invalid", $"\"{arena}\" is not a range of Arena versions (like \">=8.3.0 <9.0.0\").");
        }
        var hostApi = Int(doc["hostApi"], "hostApi", HostApiVersion, 1, 1000);
        if (hostApi > HostApiVersion)
        {
            throw Fail("manifest.host_api.unsupported", $"hostApi {hostApi} is newer than this Arena's ({HostApiVersion}).");
        }

        var scopes = new List<PluginScope>();
        var permissions = List(doc["permissions"], "permissions");
        for (var i = 0; i < permissions.Count; i++)
        {
            var p = permissions[i];
            if (p is JsonObject o && o.Count == 1 && o["network"] is { } hosts)
            {
                var list = Strings(hosts, $"permissions[{i}].network").Select(h => h.ToLowerInvariant()).ToArray();
                if (list.Length == 0 || list.Any(h => !PluginRules.IsHost(h)))
                {
                    throw Fail("manifest.permission.network_hosts", "network names host names only (no addresses, schemes or paths), at least one.");
                }
                scopes.Add(new PluginScope("network", list));
            }
            else if (Str(p) is { } s)
            {
                if (s == "network")
                {
                    throw Fail("manifest.permission.network_hosts", "network names its hosts: - network: [api.example.com]");
                }
                if (!PluginRules.IsScope(s))
                {
                    throw Fail("manifest.permission.unknown", $"{s} is not a permission plugins may ask for.");
                }
                scopes.Add(new PluginScope(s, []));
            }
            else
            {
                throw Fail("manifest.permission.unknown", $"permissions[{i}] must be a permission's name, or network: [hosts].");
            }
        }
        if (scopes.GroupBy(s => s.Scope).FirstOrDefault(g => g.Count() > 1) is { } twiceScope)
        {
            throw Fail("manifest.permission.duplicate", $"{twiceScope.Key} is asked for twice.");
        }
        var has = scopes.Select(s => s.Scope).ToHashSet();

        var settings = new List<PluginSetting>();
        foreach (var (x, i) in List(doc["settings"], "settings").Select((x, i) => (x, i)))
        {
            var o = Map(x, $"settings[{i}]");
            var key = Str(o["key"]);
            if (key is null || !SettingKey().IsMatch(key))
            {
                throw Fail("manifest.setting.key", $"settings[{i}] needs a key of lowercase letters, digits and underscores.");
            }
            if (settings.Any(s => s.Key == key))
            {
                throw Fail("manifest.setting.duplicate", $"The setting {key} is there twice.");
            }
            var type = Str(o["type"]) ?? "text";
            if (type is not ("text" or "url" or "secret" or "number" or "boolean" or "select"))
            {
                throw Fail("manifest.setting.type", $"{type} is not a setting type (text, url, secret, number, boolean, select).");
            }
            var options = type == "select" ? Strings(o["options"], $"settings[{i}].options") : [];
            if (type == "select" && options.Length == 0)
            {
                throw Fail("manifest.setting.options", $"The select setting {key} lists no options.");
            }
            if (o["default"] is not null && type == "secret")
            {
                throw Fail("manifest.setting.secret_default", $"The secret {key} cannot have a default: secrets are never in a package.");
            }
            settings.Add(new PluginSetting(key, Str(o["title"]) ?? key, type, Bool(o["required"], $"settings[{i}].required"), Str(o["help"]))
            {
                Default = Str(o["default"]), Options = options,
            });
        }

        PluginServer? server = null;
        if (doc["server"] is { } s0)
        {
            var o = Map(s0, "server");
            server = new PluginServer(File(o["entry"], "server.entry"), Int(o["memoryMb"], "server.memoryMb", 128, 16, 4096),
                Int(o["timeoutMs"], "server.timeoutMs", 10000, 100, 300000), Int(o["concurrency"], "server.concurrency", 4, 1, 64));
        }
        void NeedsServer(string what)
        {
            if (server is null)
            {
                throw Fail("manifest.server.required", $"{what} run in the plugin's server code: the manifest needs server: entry:.");
            }
        }

        string? openapi = null, mcp = null, sandbox = null, url = null;
        var functions = new List<PluginFunction>();
        if (doc["tools"] is { } t0)
        {
            var t = Map(t0, "tools");
            var sources = ToolSources.Where(k => t[k] is not null).ToList();
            if (sources.Count > 1)
            {
                throw Fail("manifest.tools.sources", $"The tools come from one place, not {string.Join(" and ", sources)}.");
            }
            if (t["openapi"] is not null)
            {
                openapi = File(t["openapi"], "tools.openapi");
            }
            if (t["sandbox"] is not null)
            {
                sandbox = File(t["sandbox"], "tools.sandbox");
            }
            if (t["mcp"] is not null)
            {
                mcp = Str(t["mcp"]);
                if (mcp is null || !(mcp.StartsWith("https://", StringComparison.Ordinal) || mcp.StartsWith("http://", StringComparison.Ordinal)))
                {
                    throw Fail("manifest.tools.mcp", "tools.mcp is the MCP server's http(s) address.");
                }
            }
            url = Str(t["url"]);
            if (t["functions"] is not null)
            {
                NeedsServer("Tools with functions");
                foreach (var (f0, i) in List(t["functions"], "tools.functions").Select((f, i) => (f, i)))
                {
                    var f = Map(f0, $"tools.functions[{i}]");
                    var fname = Str(f["name"]);
                    if (fname is null || !FunctionName().IsMatch(fname))
                    {
                        throw Fail("manifest.function.name", $"tools.functions[{i}] needs a name of lowercase letters, digits and underscores, starting with a letter.");
                    }
                    if (functions.Any(x => x.Name == fname))
                    {
                        throw Fail("manifest.function.duplicate", $"The function {fname} is there twice.");
                    }
                    var description = Str(f["description"]) ?? throw Fail("manifest.function.description", $"The function {fname} needs a description.");
                    if (f["parameters"] is not null and not JsonObject)
                    {
                        throw Fail("manifest.type", $"tools.functions[{i}].parameters must be a JSON Schema object.");
                    }
                    functions.Add(new PluginFunction(fname, description, Bool(f["writes"], $"tools.functions[{i}].writes"), f["parameters"]?.DeepClone() as JsonObject));
                }
            }
        }

        var routes = new List<PluginRoute>();
        foreach (var (r0, i) in List(doc["routes"], "routes").Select((r, i) => (r, i)))
        {
            NeedsServer("Routes");
            var r = Map(r0, $"routes[{i}]");
            var method = (Str(r["method"]) ?? "GET").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                throw Fail("manifest.route.method", $"{method} is not GET, POST, PUT, PATCH or DELETE.");
            }
            var path = Str(r["path"]);
            if (path is null || !RoutePath().IsMatch(path) || path.Split('/').Contains(".."))
            {
                throw Fail("manifest.route.path", $"routes[{i}] needs a path like /tickets/{{id}}.");
            }
            var access = Str(r["access"]) ?? "person";
            if (access is not ("person" or "manager"))
            {
                throw Fail("manifest.route.access", $"routes[{i}].access is person or manager.");
            }
            if (routes.Any(x => x.Method == method && x.Path == path))
            {
                throw Fail("manifest.route.duplicate", $"{method} {path} is there twice.");
            }
            routes.Add(new PluginRoute(method, path, access));
        }

        var hooks = new List<PluginHook>();
        foreach (var (h0, i) in List(doc["hooks"], "hooks").Select((h, i) => (h, i)))
        {
            NeedsServer("Hooks");
            var h = Map(h0, $"hooks[{i}]");
            var on = Str(h["on"]) ?? "";
            if (!PluginRules.Hooks.TryGetValue(on, out var need))
            {
                throw Fail("manifest.hook.unknown", $"{(on.Length > 0 ? on : "(none)")} is not a hook ({string.Join(", ", PluginRules.Hooks.Keys)}).");
            }
            var mode = Str(h["mode"]) ?? "observe";
            if (mode is not ("observe" or "modify"))
            {
                throw Fail("manifest.hook.mode", $"hooks[{i}].mode is observe or modify.");
            }
            var scope = mode == "modify" ? need.Modify : need.Observe;
            if (!has.Contains(scope))
            {
                throw Fail("manifest.hook.scope", $"The {mode} hook {on} needs the permission {scope}.");
            }
            var failClosed = Bool(h["failClosed"], $"hooks[{i}].failClosed");
            if (failClosed && !has.Contains("hooks.block"))
            {
                throw Fail("manifest.hook.scope", "A hook that fails closed needs the permission hooks.block.");
            }
            if (hooks.Any(x => x.On == on))
            {
                throw Fail("manifest.hook.duplicate", $"The hook {on} is there twice.");
            }
            hooks.Add(new PluginHook(on, mode, failClosed, Int(h["timeoutMs"], $"hooks[{i}].timeoutMs", 2000, 50, 10000)));
        }

        var events = new List<string>();
        foreach (var e in Strings(doc["events"], "events"))
        {
            NeedsServer("Events");
            if (!PluginRules.Events.TryGetValue(e, out var need))
            {
                throw Fail("manifest.event.unknown", $"{e} is not an event ({string.Join(", ", PluginRules.Events.Keys)}).");
            }
            if (need is not null && !has.Contains(need))
            {
                throw Fail("manifest.event.scope", $"The event {e} needs the permission {need}.");
            }
            if (!events.Contains(e))
            {
                events.Add(e);
            }
        }

        var jobs = new List<PluginJobSpec>();
        foreach (var (j0, i) in List(doc["jobs"], "jobs").Select((j, i) => (j, i)))
        {
            NeedsServer("Jobs");
            var j = Map(j0, $"jobs[{i}]");
            var jname = Str(j["name"]);
            if (jname is null || !PluginName().IsMatch(jname))
            {
                throw Fail("manifest.job.name", $"jobs[{i}] needs a name of lowercase letters, digits and dashes.");
            }
            if (jobs.Any(x => x.Name == jname))
            {
                throw Fail("manifest.job.duplicate", $"The job {jname} is there twice.");
            }
            var cron = Str(j["cron"]) ?? "";
            if (Schedules.Cron.Parse(cron).Problem is { } wrong)
            {
                throw Fail("manifest.job.cron", $"The job {jname}'s cron: {wrong}");
            }
            jobs.Add(new PluginJobSpec(jname, cron, Str(j["description"])));
        }

        var ui = ReadUi(functions);

        var auth = doc["auth"] as JsonObject ?? [];
        var person = Str(auth["per_person"]) switch
        {
            null or "none" => null,
            "api_key" => "api_key",
            "oauth2" => "oauth2",
            var other => throw Fail("manifest.auth.per_person", $"auth: per_person: {other} is not none, api_key or oauth2."),
        };
        PluginOAuth? oauth = null;
        if (person == "oauth2")
        {
            var o = auth["oauth2"] as JsonObject;
            if (o is null || Str(o["authorize"]) is not { } authorize || Str(o["token"]) is not { } token)
            {
                throw Fail("manifest.auth.oauth2", "auth: oauth2: says where people sign in (authorize, token, scopes).");
            }
            oauth = new PluginOAuth(authorize, token, Strings(o["scopes"], "auth.oauth2.scopes"));
        }
        string[] Files(string key) => [.. List(doc[key], key).Select((x, i) => File(x, $"{key}[{i}]"))];
        var toolsets = List(doc["toolsets"], "toolsets").Select((t, i) =>
        {
            var o = t as JsonObject;
            var n = (o is null ? null : Str(o["name"])) ?? throw Fail("manifest.toolset.name", $"toolsets[{i}] has no name.");
            return new PluginToolset(n, Str(o!["description"]), Strings(o["tools"], $"toolsets[{i}].tools"));
        }).ToArray();
        var tasks = new List<PluginTask>();
        foreach (var (t, i) in List(doc["tasks"], "tasks").Select((t, i) => (t, i)))
        {
            var o = t as JsonObject;
            var task = (o is null ? null : Str(o["name"])) ?? throw Fail("manifest.task.name", $"tasks[{i}] has no name.");
            if (tasks.Any(x => x.Name == task))
            {
                throw Fail("manifest.task.duplicate", $"The task {task} is there twice.");
            }
            var cron = Str(o!["cron"]) ?? "";
            if (Schedules.Cron.Parse(cron).Problem is { } wrong)
            {
                throw Fail("manifest.task.cron", $"The task {task}'s cron: {wrong}");
            }
            var prompt = Str(o["prompt"]) ?? throw Fail("manifest.task.prompt", $"The task {task} has no prompt.");
            tasks.Add(new PluginTask(task, Str(o["description"]), prompt, cron, Strings(o["tools"], $"tasks[{i}].tools")));
        }
        var skills = Files("skills");
        var assistants = Files("assistants");
        if (server is null && openapi is null && mcp is null && sandbox is null && !ui.Any && skills.Length == 0 && assistants.Length == 0 && tasks.Count == 0)
        {
            throw Fail("manifest.empty", "The plugin brings nothing: no server, tools, UI, skills, assistants or tasks.");
        }

        // Where its tools are: its server code (plugin:), the sandbox's (sandbox:), or an address (an API's or an MCP server's).
        var address = server is not null ? PluginScheme + name : sandbox is not null ? SandboxScheme + name : url ?? mcp ?? "";
        return new PluginManifest(name, version, Str(doc["title"]) ?? name, Str(doc["description"]) ?? "",
            openapi, mcp, address, person, Str(auth["header"]) ?? "Authorization", Str(auth["value"]) ?? "Bearer {token}", oauth, Str(auth["help"]),
            Strings(doc["writes"], "writes"), [.. settings])
        {
            Assistants = assistants, Safeguards = Files("safeguards"), Toolsets = toolsets, Author = Str(doc["author"]), Homepage = Str(doc["homepage"]),
            Icon = Str(doc["icon"]), Tasks = [.. tasks], Skills = skills, Sandbox = sandbox, Arena = arena, HostApi = hostApi, Scopes = [.. scopes],
            Server = server, Functions = [.. functions], Routes = [.. routes], Hooks = [.. hooks], Events = [.. events], Jobs = [.. jobs], Ui = ui,
        };
    }

    private PluginUi ReadUi(List<PluginFunction> functions)
    {
        if (doc["ui"] is not { } u0)
        {
            return PluginUi.None;
        }
        var u = Map(u0, "ui");
        string Id(JsonObject o, string path)
        {
            var id = Str(o["id"]);
            return id is not null && PluginName().IsMatch(id) ? id : throw Fail("manifest.ui.id", $"{path} needs an id of lowercase letters, digits and dashes.");
        }
        var pages = new List<PluginPage>();
        foreach (var (p0, i) in List(u["pages"], "ui.pages").Select((p, i) => (p, i)))
        {
            var p = Map(p0, $"ui.pages[{i}]");
            var id = Id(p, $"ui.pages[{i}]");
            var path = Str(p["path"]) ?? id;
            if (!PagePath().IsMatch(path) || path.Split('/').Contains(".."))
            {
                throw Fail("manifest.ui.path", $"ui.pages[{i}].path is lowercase letters, digits, dashes and slashes.");
            }
            if (pages.Any(x => x.Id == id || x.Path == path))
            {
                throw Fail("manifest.ui.duplicate", $"The page {id} (or its path) is there twice.");
            }
            pages.Add(new PluginPage(id, Str(p["title"]) ?? id, path, Html(p["entry"], $"ui.pages[{i}].entry"), Str(p["icon"])));
        }
        var menu = new List<PluginMenuItem>();
        foreach (var (m0, i) in List(u["menu"], "ui.menu").Select((m, i) => (m, i)))
        {
            var m = Map(m0, $"ui.menu[{i}]");
            var page = Str(m["page"]) ?? "";
            if (pages.All(p => p.Id != page))
            {
                throw Fail("manifest.ui.menu_page", $"ui.menu[{i}] names the page {(page.Length > 0 ? page : "(none)")}, which ui.pages does not have.");
            }
            var section = Str(m["section"]) ?? "plugins";
            if (!PluginRules.MenuSections.Contains(section))
            {
                throw Fail("manifest.ui.menu_section", $"{section} is not a menu section ({string.Join(", ", PluginRules.MenuSections)}).");
            }
            menu.Add(new PluginMenuItem(page, section, Str(m["title"])));
        }
        PluginPanel[] Panels(JsonNode? n, string path, bool slotted)
        {
            var list = new List<PluginPanel>();
            foreach (var (p0, i) in List(n, path).Select((p, i) => (p, i)))
            {
                var p = Map(p0, $"{path}[{i}]");
                var id = Id(p, $"{path}[{i}]");
                if (list.Any(x => x.Id == id))
                {
                    throw Fail("manifest.ui.duplicate", $"{id} is there twice.");
                }
                var slot = slotted ? Str(p["slot"]) ?? "" : "ide.panel";
                if (slotted && !PluginRules.PanelSlots.Contains(slot))
                {
                    throw Fail("manifest.ui.slot", $"{(slot.Length > 0 ? slot : "(none)")} is not a panel slot ({string.Join(", ", PluginRules.PanelSlots)}).");
                }
                list.Add(new PluginPanel(id, Str(p["title"]) ?? id, slot, Html(p["entry"], $"{path}[{i}].entry")));
            }
            return [.. list];
        }
        PluginAction[] Actions(JsonNode? n, string path)
        {
            var list = new List<PluginAction>();
            foreach (var (a0, i) in List(n, path).Select((a, i) => (a, i)))
            {
                var a = Map(a0, $"{path}[{i}]");
                var id = Id(a, $"{path}[{i}]");
                if (list.Any(x => x.Id == id))
                {
                    throw Fail("manifest.ui.duplicate", $"{id} is there twice.");
                }
                var entry = a["entry"] is null ? null : Html(a["entry"], $"{path}[{i}].entry");
                var run = Str(a["run"]);
                if (run is not null)
                {
                    var m = Regex.Match(run, "^tool:([a-z][a-z0-9_]*)$");
                    if (!m.Success || functions.All(f => f.Name != m.Groups[1].Value))
                    {
                        throw Fail("manifest.ui.run", $"{path}[{i}].run is tool:<one of tools.functions>.");
                    }
                }
                if ((entry is null) == (run is null))
                {
                    throw Fail("manifest.ui.action", $"{path}[{i}] has an entry (a frame) or a run (a function): one of them.");
                }
                list.Add(new PluginAction(id, Str(a["title"]) ?? id, entry, run));
            }
            return [.. list];
        }
        var chat = u["chat"] is { } c0 ? Map(c0, "ui.chat") : [];
        var ide = u["ide"] is { } i0 ? Map(i0, "ui.ide") : [];
        return new PluginUi([.. pages], [.. menu], Panels(u["panels"], "ui.panels", true),
            u["settings"] is { } st ? Html(Map(st, "ui.settings")["entry"], "ui.settings.entry") : null,
            Actions(chat["composerActions"], "ui.chat.composerActions"), Actions(chat["messageActions"], "ui.chat.messageActions"),
            Panels(ide["panels"], "ui.ide.panels", false));
    }

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex SettingKey();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex FunctionName();

    [GeneratedRegex(@"^/[A-Za-z0-9/_{}.-]*$")]
    private static partial Regex RoutePath();

    [GeneratedRegex("^[a-z0-9][a-z0-9/-]{0,99}$")]
    private static partial Regex PagePath();
}
