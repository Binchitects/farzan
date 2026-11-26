using System.Globalization;
using Llm.Api.Dashboards;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// gateway.requests_hourly, the ledger by hour, and $__requests, which reads it for the dashboards' sums and counts: each
/// panel that reads it gives the very rows it gives from the ledger itself, over windows that start and end inside an
/// hour; and the rollup stays the ledger's sum as rows are added, changed, deleted and emptied.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class LedgerRollupTests(AppFixture app)
{
    private static readonly DateTimeOffset End = new(2026, 11, 20, 17, 23, 41, TimeSpan.Zero);

    /// <summary>
    /// A few thousand requests over 40 days: people (and none), models, sources, kinds, keys (and none), states (refused
    /// ones too), projects (and none), at odd seconds.
    /// </summary>
    private static async Task SeedAsync(NpgsqlConnection conn, int rows, int offset = 0)
    {
        await using var cmd = new NpgsqlCommand($"""
            insert into gateway.requests (id, started_at, first_token_at, ended_at, duration_ms, surface, call, protocol, credit_kind, person, key_alias, model,
              prompt_tokens, cached_tokens, completion_tokens, usage_estimated, cost, status, http_status, project_id)
            select gen_random_uuid(), s.at, s.at, s.at + interval '2 s', 100 + g % 7000,
                   (array['chat','api','studio','media','app'])[1 + g % 5], 'chat', 'openai', (array['chat','api','pictures'])[1 + g % 3],
                   case when g % 11 = 0 then null else 'p' || (g % 13) || '@example.test' end,
                   case when g % 4 = 0 then null else 'key-' || (g % 3) end,
                   'model-' || (g % 4), 100 + g % 3000, (g % 5) * 40, 10 + g % 700, false, ((g % 997) / 10000.0)::numeric,
                   (array['done','done','done','failed','stopped','refused'])[1 + g % 6], 200,
                   case when g % 3 = 0 then ('00000000-0000-4000-8000-' || lpad((g % 4)::text, 12, '0'))::uuid end
            from (select g, @end - ((g * 3457) % (40 * 86400) || ' seconds')::interval - ((g % 1000) || ' milliseconds')::interval as at
                  from generate_series({offset + 1}, {offset + rows}) g) s
            """, conn);
        cmd.Parameters.AddWithValue("end", End.UtcDateTime);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The rollup against the ledger summed afresh: no group that differs, none on one side only.</summary>
    private static async Task<long> DifferencesAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            with fresh as (
              select date_trunc('hour', started_at, 'UTC') as hour, person, model, surface, credit_kind, key_alias, status, project_id,
                     count(*)::bigint as requests, sum(prompt_tokens)::bigint as prompt_tokens, sum(cached_tokens)::bigint as cached_tokens,
                     sum(completion_tokens)::bigint as completion_tokens, sum(total_tokens)::bigint as total_tokens, sum(cost) as cost, sum(duration_ms)::bigint as duration_ms
              from gateway.requests group by 1, 2, 3, 4, 5, 6, 7, 8),
            kept as (select hour, person, model, surface, credit_kind, key_alias, status, project_id, requests, prompt_tokens, cached_tokens, completion_tokens,
                            total_tokens, cost, duration_ms from gateway.requests_hourly)
            select (select count(*) from (select * from fresh except select * from kept) a) + (select count(*) from (select * from kept except select * from fresh) b)
            """, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Every_panel_on_the_rollup_gives_the_rows_it_gives_from_the_ledger_itself()
    {
        var cs = app.ConnectionStringFor("rollup_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway());
        _ = f.Server;
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await SeedAsync(conn, 6000);
        }
        var ledger = f.Services.GetRequiredService<GatewayDatasource>();
        var targets = f.Services.GetRequiredService<DashboardStore>().All()
            .SelectMany(d => d.Panels.SelectMany(p => p.Targets.Where(t => t.RawSql?.Contains(SqlMacros.Requests, StringComparison.Ordinal) == true)
                .Select(t => (d, Title: p.Title ?? "", t.RawSql)))).ToList();
        Assert.True(targets.Count >= 30, $"only {targets.Count} panels read the rollup");
        // 30 and 7 days, ending and starting inside an hour, in 2-hour and daily buckets; a day in half hours (the rows alone).
        var windows = new[]
        {
            (From: End.AddDays(-30).AddMinutes(-17), To: End, Interval: TimeSpan.FromHours(2)),
            (From: End.AddDays(-7).AddSeconds(-1234), To: End.AddMinutes(-31), Interval: TimeSpan.FromDays(1)),
            (From: End.AddDays(-1), To: End, Interval: TimeSpan.FromMinutes(30)),
        };
        var checkedBoth = 0;
        foreach (var (d, title, sql) in targets)
        {
            var model = d.Variables?.FirstOrDefault(v => v.Name == "model");
            string?[] choices = model is null ? [null] : ["$__all", "model-2"];
            foreach (var w in windows)
            {
                foreach (var chosen in choices)
                {
                    var vars = model is null || chosen is null ? null
                        : new Dictionary<string, VariableValue>(StringComparer.Ordinal) { ["model"] = new VariableValue(model, [chosen], []) };
                    var fast = SqlMacros.Expand(sql!, w.From, w.To, w.Interval, vars, out var p1);
                    var slow = SqlMacros.Expand(sql!, w.From, w.To, w.Interval, vars, out var p2, rollup: false);
                    var a = await ledger.QueryAsync(fast, CancellationToken.None, p1);
                    var b = await ledger.QueryAsync(slow, CancellationToken.None, p2);
                    Assert.True(Rows(a).SequenceEqual(Rows(b)), $"{d.Uid} / {title}, {w.From:O} to {w.To:O} by {w.Interval}, model {chosen}: the rollup's rows differ\n{string.Join("\n", Rows(a).Take(5))}\n--\n{string.Join("\n", Rows(b).Take(5))}");
                    checkedBoth += fast.Contains("requests_hourly", StringComparison.Ordinal) ? 1 : 0;
                }
            }
        }
        // Most asks went through the rollup (the half-hour buckets do not).
        Assert.True(checkedBoth > targets.Count, $"only {checkedBoth} asks used the rollup");
    }

    /// <summary>A result's rows as text, in order where its SQL orders them, else sorted; numbers by their value.</summary>
    private static List<string> Rows(TableResult t) => [.. t.Rows.Select(r => string.Join(" | ", r.Select(v => v switch
    {
        null => "null",
        decimal m => m.ToString("0.#########", CultureInfo.InvariantCulture),
        double x => x.ToString("0.#########", CultureInfo.InvariantCulture),
        float x => ((double)x).ToString("0.#########", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    }))).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_rollup_stays_the_ledgers_sum_as_rows_are_added_changed_deleted_and_emptied()
    {
        var cs = app.ConnectionStringFor("rollupt_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway());
        _ = f.Server;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        async Task RunAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SeedAsync(conn, 3000);
        Assert.Equal(0, await DifferencesAsync(conn));
        // One at a time, as the gateway books them.
        await SeedAsync(conn, 1, offset: 3000);
        await SeedAsync(conn, 1, offset: 3001);
        Assert.Equal(0, await DifferencesAsync(conn));
        // Changed: a chat put in a project, a person attributed, tokens and cost corrected, a time moved across an hour.
        await RunAsync("update gateway.requests set project_id = '00000000-0000-4000-8000-000000000009' where person = 'p3@example.test'");
        await RunAsync("update gateway.requests set person = 'p1@example.test' where person is null and model = 'model-1'");
        await RunAsync("update gateway.requests set completion_tokens = completion_tokens + 5, cost = cost * 2 where status = 'failed'");
        await RunAsync("update gateway.requests set started_at = started_at + interval '45 minutes' where surface = 'media'");
        Assert.Equal(0, await DifferencesAsync(conn));
        // Deleted, as retention does; then all of it.
        await RunAsync("delete from gateway.requests where started_at < now() - interval '1 day' and model = 'model-3'");
        Assert.Equal(0, await DifferencesAsync(conn));
        await RunAsync("delete from gateway.requests where key_alias = 'key-2'");
        Assert.Equal(0, await DifferencesAsync(conn));
        await RunAsync("truncate gateway.requests");
        await using var count = new NpgsqlCommand("select count(*) from gateway.requests_hourly", conn);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }
}
