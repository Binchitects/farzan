using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Llm.Api.Plugins;

/// <summary>One of a plugin's settings, filled in by the admin who installs it.</summary>
/// <param name="Type">"text", "url", "secret" (stored encrypted, never shown again), "number", "boolean" or "select".</param>
public sealed record PluginSetting(string Key, string Title, string Type, bool Required, string? Help)
{
    public string? Default { get; init; }
    public string[] Options { get; init; } = [];
}

/// <summary>A toolset a plugin brings: its name, and its tools ("self" is the plugin's own; others by their ids: web, python...).</summary>
public sealed record PluginToolset(string Name, string? Description, string[] Tools);

/// <summary>A scheduled task a plugin offers: people add it to their own tasks (Tasks → From plugins), its question, when, and its tools ("self" is the plugin's).</summary>
public sealed record PluginTask(string Name, string? Description, string Prompt, string Cron, string[] Tools);

/// <summary>Where a person signs in to the plugin's service, as a template ({setting} filled in).</summary>
public sealed record PluginOAuth(string Authorize, string Token, string[] Scopes);

/// <summary>A host API scope the plugin asks for; network with the hosts it may reach.</summary>
public sealed record PluginScope(string Scope, string[] Hosts);

/// <summary>Where its server code is, and the limits it asks for.</summary>
public sealed record PluginServer(string Entry, int MemoryMb, int TimeoutMs, int Concurrency);

/// <summary>A tool for models defined in the plugin's server code.</summary>
public sealed record PluginFunction(string Name, string Description, bool Writes, JsonObject? Parameters);

/// <summary>A route of the plugin's at /api/p/{plugin}{path}: for anyone it is on for, or its managers.</summary>
public sealed record PluginRoute(string Method, string Path, string Access);

public sealed record PluginHook(string On, string Mode, bool FailClosed, int TimeoutMs);

public sealed record PluginJobSpec(string Name, string Cron, string? Description);

public sealed record PluginPage(string Id, string Title, string Path, string Entry, string? Icon);

public sealed record PluginMenuItem(string Page, string Section, string? Title);

public sealed record PluginPanel(string Id, string Title, string Slot, string Entry);

public sealed record PluginAction(string Id, string Title, string? Entry, string? Run);

public sealed record PluginUi(PluginPage[] Pages, PluginMenuItem[] Menu, PluginPanel[] Panels, string? Settings, PluginAction[] ComposerActions,
    PluginAction[] MessageActions, PluginPanel[] IdePanels)
{
    public static readonly PluginUi None = new([], [], [], null, [], [], []);
    public bool Any => Pages.Length + Panels.Length + ComposerActions.Length + MessageActions.Length + IdePanels.Length > 0 || Settings is not null;
}

/// <summary>
/// A plugin's manifest (plugin.yaml, apiVersion arena.plugins/v3), checked with the plugin framework's rules and codes (its
/// conformance suite, tests/fixtures/plugins): what it is and the Arena versions it runs on; its tools (an OpenAPI document,
/// an MCP server, a Python server in the sandbox, or functions in its own server code); how calls sign in; the host API
/// scopes it asks for; its server code (run by the plugin runtime, never in the app), routes, hooks, events and jobs; its
/// UI (pages, menu items, panels, a settings screen, chat and IDE extensions, each in a sandboxed frame); and what
/// plugins v2 brought: skills, assistants, safeguard rules, toolsets and task templates.
/// </summary>
public sealed partial record PluginManifest(
    string Name, string Version, string Title, string Description,
    string? OpenApi, string? Mcp, string Url,
    string? PersonAuth, string Header, string Value, PluginOAuth? OAuth, string? Help,
    string[] Writes, PluginSetting[] Settings)
{
    public string[] Assistants { get; init; } = [];
    public string[] Safeguards { get; init; } = [];
    public PluginToolset[] Toolsets { get; init; } = [];
    public PluginTask[] Tasks { get; init; } = [];
    /// <summary>The plugin's own MCP server, run in the sandbox: a folder in the plugin with its server.py (and the files beside it).</summary>
    public string? Sandbox { get; init; }
    public string[] Skills { get; init; } = [];
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public string? Icon { get; init; }
    /// <summary>The Arena versions it runs on.</summary>
    public string Arena { get; init; } = ">=8.3.0";
    public int HostApi { get; init; } = 1;
    public PluginScope[] Scopes { get; init; } = [];
    public PluginServer? Server { get; init; }
    public PluginFunction[] Functions { get; init; } = [];
    public PluginRoute[] Routes { get; init; } = [];
    public PluginHook[] Hooks { get; init; } = [];
    public string[] Events { get; init; } = [];
    public PluginJobSpec[] Jobs { get; init; } = [];
    public PluginUi Ui { get; init; } = PluginUi.None;

    public const string ApiVersion = "arena.plugins/v3";
    /// <summary>The host API version this Arena serves.</summary>
    public const int HostApiVersion = 1;
    /// <summary>The address of a server run in the sandbox: sandbox:{plugin}.</summary>
    public const string SandboxScheme = "sandbox:";
    /// <summary>The address of a plugin whose tools (or only its pages) are its own server code, run by the plugin runtime: plugin:{plugin}.</summary>
    public const string PluginScheme = "plugin:";
    public const string FileName = "plugin.yaml";
    public const int MostBytes = 256 * 1024;

    /// <summary>Why a manifest is refused: the framework's code for it (manifest.name.invalid ...) and the words.</summary>
    public sealed class ManifestException(string code, string message) : Exception(message)
    {
        public ManifestException(string message) : this("manifest.invalid", message)
        {
        }

        public string Code { get; } = code;
    }

    /// <summary>Whether its tools or pages are its own server code (run by the plugin runtime).</summary>
    public bool RunsCode => Server is not null;

    /// <summary>Whether the plugin is one Arena's own UI or runtime serves (v3's parts), not only tools and content.</summary>
    public bool HasOwnParts => Server is not null || Ui.Any;

    /// <summary>Every file the manifest names, for checking a package.</summary>
    public IEnumerable<string> Named()
    {
        if (Server is not null)
        {
            yield return Server.Entry;
        }
        if (OpenApi is not null)
        {
            yield return OpenApi;
        }
        if (Sandbox is not null)
        {
            yield return Sandbox;
        }
        foreach (var f in Ui.Pages.Select(p => p.Entry).Concat(Ui.Panels.Select(p => p.Entry)).Concat(Ui.IdePanels.Select(p => p.Entry))
            .Concat(Ui.ComposerActions.Concat(Ui.MessageActions).Select(a => a.Entry).OfType<string>()).Concat(Assistants).Concat(Safeguards).Concat(Skills))
        {
            yield return f;
        }
        if (Ui.Settings is not null)
        {
            yield return Ui.Settings;
        }
    }

    public static PluginManifest Parse(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MostBytes)
        {
            throw new ManifestException("manifest.too_large", $"The manifest is larger than {MostBytes / 1024} KB.");
        }
        JsonObject doc;
        try
        {
            doc = Chat.Tools.OpenApi.Parse(text);
        }
        catch (Chat.Tools.OpenApi.SpecException ex)
        {
            throw new ManifestException("manifest.parse", ex.Message.Replace("OpenAPI document", "manifest", StringComparison.Ordinal));
        }
        return new ManifestReader(doc).Read();
    }

    /// <summary>"1.10.0" over "1.9.2": versions compared part by part, numbers as numbers; a prerelease before its release.</summary>
    public static int CompareVersions(string a, string b)
    {
        static (string[] Core, string? Pre) Split(string v)
        {
            v = v.TrimStart('v');
            var plus = v.IndexOf('+');
            if (plus >= 0)
            {
                v = v[..plus];
            }
            var dash = v.IndexOf('-');
            return dash >= 0 ? (v[..dash].Split('.'), v[(dash + 1)..]) : (v.Split('.'), null);
        }
        static int Part(string p, string q) => int.TryParse(p, out var m) && int.TryParse(q, out var n) ? m.CompareTo(n) : string.CompareOrdinal(p, q);
        var (x, xp) = Split(a);
        var (y, yp) = Split(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = Part(i < x.Length ? x[i] : "0", i < y.Length ? y[i] : "0");
            if (c != 0)
            {
                return c;
            }
        }
        if (xp is null || yp is null)
        {
            return (xp is null ? 0 : -1) - (yp is null ? 0 : -1);
        }
        var (xs, ys) = (xp.Split('.'), yp.Split('.'));
        for (var i = 0; i < Math.Max(xs.Length, ys.Length); i++)
        {
            if (i >= xs.Length || i >= ys.Length)
            {
                return i >= xs.Length ? -1 : 1;
            }
            var c = Part(xs[i], ys[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    /// <summary>"{gitlab_url}/api/v4" with the settings' values put in.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v.TrimEnd('/') : m.Value);

    public static bool IsVersion(string v) => SemVer().IsMatch(v);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$")]
    internal static partial Regex PluginName();

    [GeneratedRegex(@"\{([a-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();
}
