using System.Text.RegularExpressions;

namespace Llm.Api.Plugins;

/// <summary>
/// The plugin framework's lists, as this Arena serves them (host API v1): the scopes a plugin may ask for and what each
/// lets it do, the events and the scope each needs, the hooks, the panel slots and the menu's sections. The framework's
/// @farzan-arena/plugin-manifest has the same; the conformance tests hold the two together.
/// </summary>
public static partial class PluginRules
{
    public sealed record ScopeInfo(string Scope, string Text, string Risk);

    public static readonly IReadOnlyList<ScopeInfo> Scopes =
    [
        new("chats.read", "Read the chats of the person using it: titles and messages.", "medium"),
        new("chats.write", "Start chats and post messages in them as the person using it.", "medium"),
        new("chats.modify", "Change a message before the model reads it, or the model's answer before the person does (hooks in modify mode).", "high"),
        new("projects.read", "Read the projects of the person using it, their goals and tasks.", "low"),
        new("projects.write", "Add and change tasks in the projects of the person using it.", "medium"),
        new("models.use", "Ask the models, as the person using it, on their credit and under their safeguards.", "medium"),
        new("knowledge.search", "Search the company knowledge the person using it may read.", "medium"),
        new("files.read", "Read the person's files.", "medium"),
        new("files.write", "Save files among the person's files.", "medium"),
        new("people.read", "See people: their names, emails and groups.", "medium"),
        new("groups.read", "See groups and who is in them.", "low"),
        new("settings.read", "Read the company's name, language and branding.", "low"),
        new("storage", "Keep its own data in Arena (for everyone, or for each person), within its quota.", "low"),
        new("notify", "Send notes to the bell of people who use it, and toasts in the page they are on.", "low"),
        new("hooks.block", "Stop a message or a tool call when its hook fails or says no (fail closed).", "high"),
        new("network", "Reach the internet hosts named here, and only those, through Arena.", "high"),
    ];

    public static bool IsScope(string scope) => Scopes.Any(s => s.Scope == scope);

    /// <summary>Each event, and the scope a plugin needs to hear it (null: none).</summary>
    public static readonly IReadOnlyDictionary<string, string?> Events = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["chat.created"] = "chats.read",
        ["chat.message.created"] = "chats.read",
        ["project.created"] = "projects.read",
        ["project.task.changed"] = "projects.read",
        ["file.uploaded"] = "files.read",
        ["person.created"] = "people.read",
        ["group.member.added"] = "groups.read",
        ["plugin.installed"] = null,
        ["plugin.settings.changed"] = null,
    };

    /// <summary>Each hook, and the scopes it needs to observe and to change the flow.</summary>
    public static readonly IReadOnlyDictionary<string, (string Observe, string Modify)> Hooks = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["chat.message.before_model"] = ("chats.read", "chats.modify"),
        ["chat.message.after_model"] = ("chats.read", "chats.modify"),
        ["chat.tool.before_call"] = ("chats.read", "chats.modify"),
        ["file.uploaded.before_store"] = ("files.read", "files.write"),
    };

    public static readonly string[] PanelSlots = ["chat.sidebar", "project.sidebar", "admin.dashboard", "home.card"];
    public static readonly string[] MenuSections = ["everyday", "build", "plugins", "admin"];

    /// <summary>The host API's methods and the scope each needs (null: none beyond being installed).</summary>
    public static readonly IReadOnlyDictionary<string, string?> Methods = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["version"] = null, ["capabilities"] = null,
        ["chats.list"] = "chats.read", ["chats.get"] = "chats.read", ["chats.messages"] = "chats.read", ["chats.post"] = "chats.write", ["chats.create"] = "chats.write",
        ["projects.list"] = "projects.read", ["projects.get"] = "projects.read", ["projects.tasks"] = "projects.read",
        ["projects.addTask"] = "projects.write", ["projects.updateTask"] = "projects.write",
        ["models.list"] = "models.use", ["models.complete"] = "models.use",
        ["knowledge.search"] = "knowledge.search", ["knowledge.sources"] = "knowledge.search",
        ["files.list"] = "files.read", ["files.get"] = "files.read", ["files.content"] = "files.read", ["files.put"] = "files.write",
        ["people.me"] = null, ["people.get"] = "people.read", ["people.search"] = "people.read",
        ["groups.list"] = "groups.read", ["groups.members"] = "groups.read",
        ["settings.get"] = null, ["settings.company"] = "settings.read",
        ["storage.get"] = "storage", ["storage.set"] = "storage", ["storage.delete"] = "storage", ["storage.list"] = "storage",
        ["http.fetch"] = "network", ["notify.bell"] = "notify", ["audit.write"] = null,
    };

    /// <summary>The methods that act for a person, and are refused for a call that is for nobody (a job, an event about nobody).</summary>
    public static readonly IReadOnlySet<string> PersonMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "chats.list", "chats.get", "chats.messages", "chats.post", "chats.create",
        "projects.list", "projects.get", "projects.tasks", "projects.addTask", "projects.updateTask",
        "models.list", "models.complete", "knowledge.search", "knowledge.sources",
        "files.list", "files.get", "files.content", "files.put", "people.me",
    };

    /// <summary>The grant key a scope is kept as among a plugin's granted permissions.</summary>
    public static string GrantKey(string scope) => "scope:" + scope;

    /// <summary>The grant key of one network host.</summary>
    public static string HostKey(string host) => "host:" + host;

    /// <summary>A host a plugin may name: a DNS name (optionally *.), optionally with a port; no addresses, schemes or paths.</summary>
    public static bool IsHost(string host) => Host().IsMatch(host);

    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(:\d{1,5})?$")]
    private static partial Regex Host();

    // ---- Version ranges: ">=8.3.0 <9.0.0", "^8.3", "~1.2.0", "8.x", "1.0.0 - 2.0.0", "a || b" -----------------------------

    private sealed record V(int Major, int Minor, int Patch, string? Pre);

    private static V? Partial(string s, out bool minorGiven, out bool patchGiven)
    {
        minorGiven = patchGiven = false;
        var m = PartialRx().Match(s);
        if (!m.Success)
        {
            return null;
        }
        static int? Num(Group g) => !g.Success || g.Value is "x" or "X" or "*" ? null : int.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (Num(m.Groups[1]) is not { } major)
        {
            return new V(-1, 0, 0, null);
        }
        var minor = Num(m.Groups[2]);
        var patch = minor is null ? null : Num(m.Groups[3]);
        minorGiven = minor is not null;
        patchGiven = patch is not null;
        return new V(major, minor ?? 0, patch ?? 0, m.Groups[4].Success ? m.Groups[4].Value : null);
    }

    [GeneratedRegex(@"^v?(\d+|[xX*])(?:\.(\d+|[xX*]))?(?:\.(\d+|[xX*]))?(?:-([0-9A-Za-z.-]+))?$")]
    private static partial Regex PartialRx();

    private static List<(string Op, string Version)>? Comparators(string part)
    {
        var tokens = Regex.Replace(part.Trim(), @"(>=|<=|>|<|=|\^|~)\s+", "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<(string, string)>();
        static string S(int a, int b, int c, string? pre = null) => $"{a}.{b}.{c}{(pre is null ? "" : "-" + pre)}";
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i + 2 < tokens.Length && tokens[i + 1] == "-")
            {
                var a = Partial(tokens[i], out _, out _);
                var b = Partial(tokens[i + 2], out var bm, out var bp);
                if (a is null || b is null || a.Major < 0 || b.Major < 0)
                {
                    return null;
                }
                list.Add((">=", S(a.Major, a.Minor, a.Patch, a.Pre)));
                list.Add(!bm ? ("<", S(b.Major + 1, 0, 0)) : !bp ? ("<", S(b.Major, b.Minor + 1, 0)) : ("<=", S(b.Major, b.Minor, b.Patch, b.Pre)));
                i += 2;
                continue;
            }
            var m = Regex.Match(tokens[i], @"^(>=|<=|>|<|=|\^|~)?(.*)$");
            var op = m.Groups[1].Value;
            var p = Partial(m.Groups[2].Value, out var minorGiven, out var patchGiven);
            if (p is null)
            {
                return null;
            }
            if (p.Major < 0)
            {
                continue;
            }
            var lo = S(p.Major, p.Minor, p.Patch, p.Pre);
            switch (op)
            {
                case "^":
                    list.Add((">=", lo));
                    list.Add(("<", p.Major > 0 || !minorGiven ? S(p.Major + 1, 0, 0) : p.Minor > 0 || !patchGiven ? S(0, p.Minor + 1, 0) : S(0, 0, p.Patch + 1)));
                    break;
                case "~":
                    list.Add((">=", lo));
                    list.Add(("<", !minorGiven ? S(p.Major + 1, 0, 0) : S(p.Major, p.Minor + 1, 0)));
                    break;
                case "" or "=":
                    if (!minorGiven)
                    {
                        list.Add((">=", lo));
                        list.Add(("<", S(p.Major + 1, 0, 0)));
                    }
                    else if (!patchGiven)
                    {
                        list.Add((">=", lo));
                        list.Add(("<", S(p.Major, p.Minor + 1, 0)));
                    }
                    else
                    {
                        list.Add(("=", lo));
                    }
                    break;
                default:
                    list.Add((op, lo));
                    break;
            }
        }
        return list;
    }

    public static bool IsRange(string range) => range.Trim().Length > 0 && range.Split("||").All(p => Comparators(p) is not null);

    /// <summary>Whether the version is in the range; a prerelease matches only a comparator on its own MAJOR.MINOR.PATCH.</summary>
    public static bool Satisfies(string version, string range)
    {
        if (!PluginManifest.IsVersion(version))
        {
            return false;
        }
        var core = version.TrimStart('v').Split('-', '+')[0];
        var pre = version.Contains('-');
        return range.Split("||").Any(part =>
        {
            if (Comparators(part) is not { } cs)
            {
                return false;
            }
            var ok = cs.All(c =>
            {
                var d = PluginManifest.CompareVersions(version, c.Version);
                return c.Op switch { ">=" => d >= 0, "<=" => d <= 0, ">" => d > 0, "<" => d < 0, _ => d == 0 };
            });
            return ok && (!pre || cs.Any(c => c.Version.Contains('-') && c.Version.Split('-')[0] == core));
        });
    }
}
