using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack that has data to v8.2: decks keep their slides and get the built-in look; the Decks tool, now the
/// canvas's slides, is "canvas" wherever it was chosen (once), and its settings, groups' settings and rules are gone;
/// requests imported from LiteLLM whose first token was their end have none.
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
            // Two requests imported from LiteLLM: one not streamed (its first token its end), one streamed; and the app's own.
            await using var ledger = new NpgsqlCommand("""
                insert into gateway.requests (id, started_at, first_token_at, ended_at, duration_ms, surface, call, protocol, credit_kind, model, prompt_tokens,
                  cached_tokens, completion_tokens, usage_estimated, cost, status, http_status, source_id) values
                  ('01a1b3fb-0000-7000-8000-0000000000a1', '2026-10-01 10:00:00Z', '2026-10-01 10:00:09Z', '2026-10-01 10:00:09Z', 9000, 'api', 'chat', 'openai', 'api', 'qwen', 10, 0, 500, false, 0, 'done', 200, 'lite-1'),
                  ('01a1b3fb-0000-7000-8000-0000000000a2', '2026-10-01 10:00:00Z', '2026-10-01 10:00:01Z', '2026-10-01 10:00:09Z', 9000, 'api', 'chat', 'openai', 'api', 'qwen', 10, 0, 500, false, 0, 'done', 200, 'lite-2'),
                  ('01a1b3fb-0000-7000-8000-0000000000a3', '2026-10-01 10:00:00Z', '2026-10-01 10:00:09Z', '2026-10-01 10:00:09Z', 9000, 'api', 'chat', 'openai', 'api', 'qwen', 10, 0, 500, false, 0, 'done', 200, null)
                """, conn);
            await ledger.ExecuteNonQueryAsync();
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
        // An imported answer not streamed has no first token; a streamed one keeps its own; the app's own rows are as they were.
        var firsts = await db.GatewayRequests.AsNoTracking().Where(r => r.Model == "qwen").OrderBy(r => r.Id).Select(r => r.FirstTokenAt).ToListAsync();
        Assert.Equal([null, new DateTimeOffset(2026, 10, 1, 10, 0, 1, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 10, 0, 9, TimeSpan.Zero)], firsts);
    }
}
