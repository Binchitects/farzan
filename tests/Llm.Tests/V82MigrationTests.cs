using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has data to v8.2: decks keep their slides and get the built-in look; the Decks tool, now the
/// canvas's slides, is "canvas" wherever it was chosen (once), and its settings, groups' settings and rules are gone.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class V82MigrationTests(AppFixture app)
{
    [Fact]
    public async Task Decks_keep_their_slides_and_the_Decks_tool_is_the_canvas_wherever_it_was_chosen()
    {
        var cs = app.ConnectionStringFor("v82_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        var before = all[all.FindIndex(m => m.EndsWith("_DeckLook", StringComparison.Ordinal)) - 1];
        await db.GetService<IMigrator>().MigrateAsync(before);

        var (person, deck, chat, both, task, set, rule) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "AspNetUsers", new() { ["Id"] = person, ["UserName"] = "kim", ["NormalizedUserName"] = "KIM" });
            await V61MigrationTests.InsertAsync(conn, "decks", new() { ["Id"] = deck, ["OwnerId"] = person, ["Title"] = "Board", ["Slides"] = """[{"layout":"title","title":"Board"}]""" });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = chat, ["UserId"] = person, ["Tools"] = new[] { "web", "decks", "calculator" } });
            await V61MigrationTests.InsertAsync(conn, "conversations", new() { ["Id"] = both, ["UserId"] = person, ["Tools"] = new[] { "canvas", "decks" }, ["LoadedTools"] = new[] { "decks" } });
            await V61MigrationTests.InsertAsync(conn, "scheduled_tasks", new() { ["Id"] = task, ["UserId"] = person, ["Tools"] = new[] { "decks" } });
            await V61MigrationTests.InsertAsync(conn, "toolsets", new() { ["Id"] = set, ["Name"] = "Writing", ["Tools"] = new[] { "decks", "web" } });
            await V61MigrationTests.InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "decks", ["Enabled"] = true });
            await V61MigrationTests.InsertAsync(conn, "tool_settings", new() { ["ToolId"] = "web", ["Enabled"] = true });
            await V61MigrationTests.InsertAsync(conn, "access_rules", new() { ["Id"] = rule, ["ResourceKind"] = "tool", ["Resource"] = "decks" });
        }
        await db.Database.MigrateAsync();

        var kept = await db.Decks.AsNoTracking().SingleAsync(d => d.Id == deck);
        Assert.Equal(("""[{"layout":"title","title":"Board"}]""", "{}"), (kept.Slides, kept.Look));
        Assert.Equal(["web", "calculator", "canvas"], (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == chat)).Tools);
        var twice = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == both);
        Assert.Equal(["canvas"], twice.Tools);
        Assert.Equal(["canvas"], twice.LoadedTools);
        Assert.Equal(["canvas"], (await db.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Id == task)).Tools);
        Assert.Equal(["web", "canvas"], (await db.Toolsets.AsNoTracking().SingleAsync(t => t.Id == set)).Tools);
        Assert.Equal(["web"], await db.ToolSettings.AsNoTracking().Select(t => t.ToolId).ToListAsync());
        Assert.False(await db.AccessRules.AsNoTracking().AnyAsync(r => r.Id == rule));
    }
}
