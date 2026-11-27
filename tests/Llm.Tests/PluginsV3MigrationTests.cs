// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json.Nodes;
using Llm.Api.Plugins;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack with plugins installed by 7.x and 8.x (plugins v2) to 8.3: each plugin's manifest, and every kept
/// version's, made v3 (apiVersion first, a version like 1.2.3), so it reads, lists, updates and rolls back as before.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PluginsV3MigrationTests(AppFixture app)
{
    /// <summary>
    /// An 8.2.5 database upgraded: 8.2's migrations dated after 8.3's PluginsV3 (DropCodeWorkspaces, RequestsHourly,
    /// RequestsSpread, SkillScans) are there and PluginsV3 is not, so the upgrade applies it after them, its v2 plugins
    /// read as v3 as on any other path.
    /// </summary>
    [Fact]
    public async Task A_database_of_8_2_5_upgrades_with_plugins_v3_applied_after_8_2s_later_migrations()
    {
        var cs = app.ConnectionStringFor("plugins825_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var v3 = all.FindIndex(m => m.EndsWith("_PluginsV3", StringComparison.Ordinal));
        Assert.True(v3 > 0 && v3 < all.Count - 1, "8.2 has migrations dated after PluginsV3");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(all[v3 - 1]);
        // Each of 8.2's later migrations on its own, as 8.2.5 applied them: PluginsV3 is left out.
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            for (var i = v3 + 1; i < all.Count; i++)
            {
                var script = migrator.GenerateScript(all[i - 1], all[i]);
                await using var cmd = new NpgsqlCommand(script, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await V61MigrationTests.InsertAsync(conn, "mcp_servers", new()
            {
                ["Id"] = Guid.NewGuid(), ["Name"] = "weather", ["Url"] = "https://x.example.test", ["Plugin"] = "weather", ["PluginVersion"] = "2",
                ["Manifest"] = "name: weather\nversion: 2\ntools: { mcp: https://weather.example.test/mcp }\n",
                ["PluginPermissions"] = Array.Empty<string>(), ["Writes"] = Array.Empty<string>(), ["Tls"] = 0, ["PluginPinned"] = false, ["CreatedAt"] = DateTimeOffset.UtcNow,
            });
        }
        Assert.Equal([all[v3]], await db.Database.GetPendingMigrationsAsync());

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var weather = await db.McpServers.AsNoTracking().SingleAsync(x => x.Plugin == "weather");
        Assert.Equal("2.0.0", PluginManifest.Parse(weather.Manifest!).Version);
        // Plugins v3's own tables are there, and 8.2's later ones as they were.
        Assert.Empty(await db.PluginStates.ToListAsync());
        Assert.Empty(await db.PluginEvents.ToListAsync());
    }

    [Fact]
    public async Task Plugins_installed_before_8_3_read_as_v3_after_the_upgrade()
    {
        var cs = app.ConnectionStringFor("pluginsv3_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_PluginsV3", StringComparison.Ordinal)) - 1]);

        const string gitlab = "# The GitLab plugin\nname: gitlab-issues\nversion: \"1.1\"\ntitle: GitLab issues\ntools:\n  openapi: openapi.yaml\n  url: \"{gitlab_url}/api/v4\"\nwrites: [create_issue]\n";
        const string weather = "name: weather\nversion: 2\ntools: { mcp: https://weather.example.test/mcp }\n";
        const string units = "apiVersion: arena.plugins/v3\nname: units\nversion: 1.0.0\ntools: { sandbox: mcp }\n";
        // What plugins v2 also took: no version at all, an odd one, a setting type it did not know, a key with capitals.
        const string bare = "name: bare\ntools: { mcp: https://bare.example.test/mcp }\nsettings:\n  - { key: apiKey, title: Key, type: password }\n";
        const string odd = "name: odd\nversion: 1.2.3.4\ntools: { mcp: https://odd.example.test/mcp }\n";
        var (a, b, c, d, e) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            foreach (var (id, name, manifest) in new[] { (a, "gitlab-issues", gitlab), (b, "weather", weather), (c, "units", units), (d, "bare", bare), (e, "odd", odd) })
            {
                await V61MigrationTests.InsertAsync(conn, "mcp_servers", new()
                {
                    ["Id"] = id, ["Name"] = name, ["Url"] = "https://x.example.test", ["Plugin"] = name, ["PluginVersion"] = "1", ["Manifest"] = manifest,
                    ["PluginPermissions"] = Array.Empty<string>(), ["Writes"] = Array.Empty<string>(), ["Tls"] = 0, ["PluginPinned"] = false, ["CreatedAt"] = DateTimeOffset.UtcNow,
                });
            }
            await V61MigrationTests.InsertAsync(conn, "plugin_releases", new()
            {
                ["Id"] = Guid.NewGuid(), ["ServerId"] = a, ["Version"] = "1.1", ["FilesJson"] = new JsonObject { ["plugin.yaml"] = gitlab, ["openapi.yaml"] = "openapi: 3.0.0" }.ToJsonString(),
                ["Permissions"] = Array.Empty<string>(), ["InstalledAt"] = DateTimeOffset.UtcNow,
            });
        }
        await db.Database.MigrateAsync();

        var servers = await db.McpServers.AsNoTracking().ToDictionaryAsync(s => s.Id);
        var g = PluginManifest.Parse(servers[a].Manifest!);
        Assert.Equal(("gitlab-issues", "1.1.0", "create_issue"), (g.Name, g.Version, string.Join(",", g.Writes)));
        Assert.StartsWith("apiVersion: arena.plugins/v3\n# The GitLab plugin\n", servers[a].Manifest!, StringComparison.Ordinal);
        Assert.Equal("2.0.0", PluginManifest.Parse(servers[b].Manifest!).Version);
        Assert.Equal(units, servers[c].Manifest);
        var bareRead = PluginManifest.Parse(servers[d].Manifest!);
        Assert.Equal(("0.0.0", "apiKey", "text"), (bareRead.Version, bareRead.Settings[0].Key, bareRead.Settings[0].Type));
        Assert.Equal("0.0.0", PluginManifest.Parse(servers[e].Manifest!).Version);
        var release = await db.PluginReleases.AsNoTracking().SingleAsync(r => r.ServerId == a);
        var files = JsonNode.Parse(release.FilesJson)!.AsObject();
        Assert.Equal("1.1.0", PluginManifest.Parse(files["plugin.yaml"]!.GetValue<string>()).Version);
        Assert.Equal("openapi: 3.0.0", files["openapi.yaml"]!.GetValue<string>());
        // Nothing of plugins v3's own until an admin gives it: no state row means on, as it was.
        Assert.Empty(await db.PluginStates.ToListAsync());
    }
}
