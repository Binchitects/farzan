using System.Text.Json;
using Llm.Api.Chat.Tools;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Safeguards;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plugins;

/// <summary>One thing a plugin asks for, as the admin grants it at install: its key, and a sentence that says it.</summary>
public sealed record PluginGrant(string Key, string Text);

/// <summary>
/// What a plugin brings besides its tools, kept in step with its version: its assistants (for everyone), its safeguard rules
/// (added in monitor mode, for an admin to enforce) and its toolsets; all of it goes when the plugin does. And the
/// permissions it asks for, from what it brings.
/// </summary>
public static class PluginContent
{
    /// <summary>What installing the package grants it, each said in a sentence.</summary>
    public static List<PluginGrant> Permissions(PluginPackage package, string? url = null)
    {
        var m = package.Manifest;
        var list = new List<PluginGrant>();
        var host = Uri.TryCreate(url ?? m.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host : null;
        if (m.Mcp is not null || m.OpenApi is not null || m.Sandbox is not null || m.Functions.Length > 0)
        {
            list.Add(new("tools", m.Mcp is not null ? $"Adds an MCP server's tools to the chat{(host is null || host.Contains('{') ? "" : $", at {host}")}."
                : m.Functions.Length > 0 ? $"Adds tools to the chat from its own code: {string.Join(", ", m.Functions.Select(f => f.Name))}."
                : m.Sandbox is not null ? "Adds tools to the chat, from its own Python server in the sandbox." : "Adds tools to the chat, from its API document."));
        }
        if (m.Server is not null)
        {
            list.Add(new("code", $"Runs its own server code in Arena's plugin runtime: apart from Arena and every other plugin, no network of its own, at most {m.Server.MemoryMb} MB, each call within {m.Server.TimeoutMs / 1000.0:0.#} s."));
        }
        if (m.Ui.Any)
        {
            list.Add(new("ui", $"Adds {string.Join(", ", new[] { (m.Ui.Pages.Length, "page"), (m.Ui.Panels.Length + m.Ui.IdePanels.Length, "panel"), (m.Ui.ComposerActions.Length + m.Ui.MessageActions.Length, "chat action") }.Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}{(x.Item1 > 1 ? "s" : "")}"))}{(m.Ui.Settings is null ? "" : " and a settings screen")} to Arena, each in a sandboxed frame."));
        }
        foreach (var scope in m.Scopes)
        {
            if (scope.Scope == "network")
            {
                list.AddRange(scope.Hosts.Select(h => new PluginGrant(PluginRules.HostKey(h), $"Reaches {h} through Arena (http.fetch), and no other host.")));
            }
            list.Add(new(PluginRules.GrantKey(scope.Scope), PluginRules.Scopes.First(x => x.Scope == scope.Scope).Text));
        }
        if (host is not null)
        {
            list.Add(new($"network:{host}", $"Sends what the chat asks of it to {host}."));
        }
        if (m.PersonAuth is not null)
        {
            list.Add(new("accounts", m.PersonAuth == "oauth2" ? "Each person signs in to its service, and it acts as them." : "Each person gives it their own API key, and it acts as them."));
        }
        if (m.Writes.Length > 0)
        {
            list.Add(new("writes", $"Can change things: {string.Join(", ", m.Writes)} (each call asks the person first)."));
        }
        if (Chat.SkillLibrary.ChatSkillsOf(package) is { Count: > 0 } skills)
        {
            list.Add(new("skills", $"Adds {skills.Count} skill{(skills.Count > 1 ? "s" : "")} for the chat: {string.Join(", ", skills.Select(p => "/" + p.Name))}."));
        }
        if (package.Assistants.Count > 0)
        {
            list.Add(new("assistants", $"Adds {package.Assistants.Count} assistant{(package.Assistants.Count > 1 ? "s" : "")} for everyone: {string.Join(", ", package.Assistants.Select(a => a.Name))}."));
        }
        if (package.Rules.Count > 0)
        {
            list.Add(new("safeguards", $"Adds {package.Rules.Count} safeguard rule{(package.Rules.Count > 1 ? "s" : "")}, in monitor mode until an admin enforces them: {string.Join(", ", package.Rules.Select(r => r.Name))}."));
        }
        if (m.Toolsets.Length > 0)
        {
            list.Add(new("toolsets", $"Adds toolsets: {string.Join(", ", m.Toolsets.Select(t => t.Name))}."));
        }
        return list;
    }

    /// <summary>Permissions that need the admin's yes beyond adding tools: everything it brings to people or to the rules.</summary>
    public static IEnumerable<string> NeedingYes(IEnumerable<PluginGrant> permissions) =>
        permissions.Select(p => p.Key).Where(k => k is "assistants" or "safeguards" or "toolsets" or "accounts" or "writes" or "code" or "ui"
            || k.StartsWith("network:", StringComparison.Ordinal) || k.StartsWith("scope:", StringComparison.Ordinal) || k.StartsWith("host:", StringComparison.Ordinal));

    /// <summary>
    /// Why the package may not be installed here, or null: an Arena version outside the range it runs on, or code or pages
    /// (plugins v3's parts, which run in Arena's runtime and frames) not signed by a trusted publisher, unless an admin
    /// allowed unsigned packages (Settings → Plugins). Tools and content only (plugins v2's parts) install as before.
    /// </summary>
    public static string? Refusal(PluginPackage package, bool allowUnsigned)
    {
        var m = package.Manifest;
        var arena = AppInfo.Current.Version.Split('-', '+')[0];
        if (!PluginRules.Satisfies(arena, m.Arena))
        {
            return $"{m.Title} {m.Version} runs on Arena {m.Arena}; this is {arena}.";
        }
        if (m.HasOwnParts && !package.Signature.Trusted && !allowUnsigned)
        {
            return package.Signature.IsSigned
                ? $"{m.Title} is signed by a key not trusted here ({package.Signature.Fingerprint}): an admin adds its publisher's key under Admin → Plugins → Trusted publishers."
                : $"{m.Title} runs code or pages in Arena and is not signed: sign it (arena-plugin package --key), or an admin allows unsigned packages (Settings → Plugins).";
        }
        return null;
    }

    /// <summary>The package checked again with the trusted publishers' keys: signed by one of them, it is trusted.</summary>
    public static async Task<PluginPackage> TrustAsync(AppDbContext db, PluginPackage package, CancellationToken ct)
    {
        if (package.Bytes is null || !package.Signature.IsSigned || package.Signature.Trusted)
        {
            return package;
        }
        var keys = await db.PluginPublishers.AsNoTracking().Select(p => p.PublicKeyPem).ToListAsync(ct);
        return package with { Signature = PluginFap.Read(package.Bytes, keys).Signature };
    }

    /// <summary>A .fap kept with its release (every file, its UI's pictures too), for the runtime, the frames and a rollback.</summary>
    public static void Keep(AppDbContext db, PluginRelease release, PluginPackage package)
    {
        if (package.Bytes is { } bytes)
        {
            db.PluginPackageBlobs.Add(new PluginPackageBlob { ReleaseId = release.Id, Bytes = bytes, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) });
        }
    }

    /// <summary>The plugin's state kept with the signer of the version installed (and, for a dev install, whose it is).</summary>
    public static async Task StateAsync(AppDbContext db, McpServer server, PluginPackage package, Guid? devOwner, CancellationToken ct)
    {
        var state = await db.PluginStates.SingleOrDefaultAsync(s => s.ServerId == server.Id, ct);
        if (state is null && !package.Manifest.HasOwnParts && devOwner is null)
        {
            return;
        }
        if (state is null)
        {
            state = new PluginState { ServerId = server.Id, DevOwnerId = devOwner };
            db.PluginStates.Add(state);
        }
        state.Signer = package.Signature.Trusted ? package.Signature.Fingerprint : null;
        state.UntrustedSigner = package.Signature.IsSigned && !package.Signature.Trusted ? package.Signature.Fingerprint : null;
    }

    /// <summary>The plugin's assistants, rules and toolsets as the package has them: added, changed, or removed when it no longer does.</summary>
    public static async Task SyncAsync(AppDbContext db, McpServer server, PluginPackage package, Guid? adminId, CancellationToken ct)
    {
        var plugin = server.Plugin!;
        var self = McpServerTool.Prefix + server.Id;
        string[] Tools(IEnumerable<string> names) => [.. names.Select(n => n == "self" ? self : n).Distinct()];

        var assistants = await db.Assistants.Where(a => a.Plugin == plugin).ToListAsync(ct);
        foreach (var a in package.Assistants)
        {
            var row = assistants.FirstOrDefault(x => x.Name == a.Name);
            if (row is null)
            {
                row = new Assistant { Name = a.Name, UserId = adminId ?? Guid.Empty, Plugin = plugin, Reach = Reach.Company };
                if (adminId is null)
                {
                    continue;
                }
                db.Assistants.Add(row);
            }
            row.Description = a.Description;
            row.Instructions = a.Instructions;
            row.Starters = [.. a.Starters];
            row.Tools = a.Tools.Length > 0 ? [.. Tools(a.Tools)] : [self];
            row.Model = a.Model;
            row.Icon = a.Icon ?? row.Icon;
            row.Color = a.Color ?? row.Color;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        db.Assistants.RemoveRange(assistants.Where(x => package.Assistants.All(a => a.Name != x.Name)));

        var rules = await db.SafeguardRules.Where(r => r.Plugin == plugin).ToListAsync(ct);
        var order = (await db.SafeguardRules.MaxAsync(r => (int?)r.Order, ct) ?? 0) + 10;
        foreach (var r in package.Rules)
        {
            var name = $"{server.Name}: {r.Name}";
            var row = rules.FirstOrDefault(x => x.Name == name);
            var adding = row is null;
            row ??= new SafeguardRule { Name = name, Detector = r.Detector, Action = r.Action, Plugin = plugin, Order = order += 10, CreatedById = adminId };
            var monitor = adding || row.Monitor;
            var enabled = adding ? r.Enabled : row.Enabled;
            Safeguards.SafeguardEndpoints.Apply(row, r with { Name = name });
            // A plugin's rule watches first: an admin makes it bite (Admin → Safeguards).
            (row.Monitor, row.Enabled) = (monitor, enabled);
            if (adding)
            {
                db.SafeguardRules.Add(row);
            }
        }
        db.SafeguardRules.RemoveRange(rules.Where(x => package.Rules.All(r => $"{server.Name}: {r.Name}" != x.Name)));

        var sets = await db.Toolsets.Where(t => t.Plugin == plugin).ToListAsync(ct);
        foreach (var t in package.Manifest.Toolsets)
        {
            var row = sets.FirstOrDefault(x => x.Name == t.Name);
            if (row is null)
            {
                if (await db.Toolsets.AnyAsync(x => x.Name == t.Name, ct))
                {
                    // An admin's own toolset has the name: theirs stays.
                    continue;
                }
                row = new Toolset { Name = t.Name, Plugin = plugin };
                db.Toolsets.Add(row);
            }
            row.Description = t.Description;
            row.Tools = [.. Tools(t.Tools.Length > 0 ? t.Tools : ["self"])];
        }
        db.Toolsets.RemoveRange(sets.Where(x => package.Manifest.Toolsets.All(t => t.Name != x.Name)));

        // The scheduled tasks it offers: people add them to their own (Automations → From plugins).
        var templates = await db.AutomationTemplates.Where(t => t.Plugin == plugin).ToListAsync(ct);
        foreach (var t in package.Manifest.Tasks)
        {
            var row = templates.FirstOrDefault(x => x.Name == t.Name);
            if (row is null)
            {
                row = new AutomationTemplate { Plugin = plugin, ServerId = server.Id, Name = t.Name, Prompt = t.Prompt, Cron = t.Cron };
                db.AutomationTemplates.Add(row);
            }
            row.ServerId = server.Id;
            row.Description = t.Description;
            row.Prompt = t.Prompt;
            row.Cron = t.Cron;
            row.Tools = [.. Tools(t.Tools.Length > 0 ? t.Tools : ["self"])];
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        db.AutomationTemplates.RemoveRange(templates.Where(x => package.Manifest.Tasks.All(t => t.Name != x.Name)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The Code Arena skills of the plugins this person may use (their tool allowed to them), from each one's version
    /// installed: a name two plugins use is the first's by plugin name.
    /// </summary>
    public static async Task<List<(PluginSkill Skill, string Plugin, string Version)>> SkillsAsync(AppDbContext db, IReadOnlyCollection<string> usable, CancellationToken ct)
    {
        var servers = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null).OrderBy(s => s.Plugin).ToListAsync(ct);
        var found = new List<(PluginSkill, string, string)>();
        foreach (var server in servers.Where(s => usable.Contains(McpServerTool.Prefix + s.Id)))
        {
            if (server.Manifest is null || PluginManifest.Parse(server.Manifest).Skills.Length == 0)
            {
                continue;
            }
            var release = await db.PluginReleases.AsNoTracking().Where(r => r.ServerId == server.Id).OrderByDescending(r => r.InstalledAt).FirstOrDefaultAsync(ct);
            if (release is null || JsonSerializer.Deserialize<Dictionary<string, string>>(release.FilesJson) is not { } files)
            {
                continue;
            }
            try
            {
                foreach (var skill in PluginCatalog.FromFiles(files).Skills.Where(k => found.All(f => f.Item1.Name != k.Name)))
                {
                    found.Add((skill, server.Plugin!, release.Version));
                }
            }
            catch (Exception ex) when (ex is PluginCatalog.PluginException or PluginManifest.ManifestException)
            {
                continue;
            }
        }
        return found;
    }

    /// <summary>Everything the plugin brought, gone with it.</summary>
    public static async Task RemoveAsync(AppDbContext db, string plugin, CancellationToken ct)
    {
        await db.Assistants.Where(a => a.Plugin == plugin).ExecuteDeleteAsync(ct);
        await db.SafeguardRules.Where(r => r.Plugin == plugin).ExecuteDeleteAsync(ct);
        await db.Toolsets.Where(t => t.Plugin == plugin).ExecuteDeleteAsync(ct);
        await db.AutomationTemplates.Where(t => t.Plugin == plugin).ExecuteDeleteAsync(ct);
    }

    /// <summary>The version installed, kept so it can be put back.</summary>
    public static PluginRelease Release(McpServer server, PluginPackage package, Guid? adminId) => new()
    {
        ServerId = server.Id, Version = package.Manifest.Version, FilesJson = JsonSerializer.Serialize(package.Files),
        Permissions = [.. Permissions(package).Select(p => p.Key)], InstalledById = adminId,
    };
}
