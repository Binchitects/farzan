using System.Globalization;
using Llm.Api.Dashboards;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// gateway.requests_hourly, the ledger by hour, and $__requests, which reads it for the dashboards' sums and counts; and
/// gateway.requests_hourly_spread, its times as a sketch, and $__spread, for their percentiles: each panel that reads them
/// gives the very rows it gives from the ledger itself, over windows that start and end inside an hour; a percentile from
/// the sketch is within 1% of the rows'; and both stay the ledger's as rows are added, changed, deleted and emptied.
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
            select gen_random_uuid(), s.at, case when g % 9 = 0 then null else s.at + ((g * 37) % 4000 || ' ms')::interval end, s.at + interval '2 s',
                   case when g % 17 = 0 then 0 else (100 + (g * 7919) % 30000) / (1 + g % 3) end,
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

    /// <summary>The spread against the ledger counted afresh in the same buckets.</summary>
    private static async Task<long> SpreadDifferencesAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            with v as (select started_at, model, status = 'refused' as refused, surface = 'media' as media, 'd'::char(1) as metric, duration_ms::float8 as ms from gateway.requests
                       union all select started_at, model, status = 'refused', surface = 'media', 'f', extract(epoch from (first_token_at - started_at)) * 1000 from gateway.requests where first_token_at is not null),
            fresh as (select date_trunc('hour', started_at, 'UTC') as hour, model, refused, media, metric, gateway.sketch_bucket(ms) as bucket, count(*)::bigint as n from v group by 1, 2, 3, 4, 5, 6),
            kept as (select hour, model, refused, media, metric, bucket, n from gateway.requests_hourly_spread)
            select (select count(*) from (select * from fresh except select * from kept) a) + (select count(*) from (select * from kept except select * from fresh) b)
            """, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_percentile_from_the_spread_is_within_one_percent_of_the_requests_own()
    {
        var cs = app.ConnectionStringFor("spread_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway());
        _ = f.Server;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await SeedAsync(conn, 8000);
        // Each quantile, overall, by model and by hour of the day, of the requests' time and of their time to the first token:
        // the sketch's against percentile_disc's of the rows (the same rank), the gap at most 1% of it.
        await using var cmd = new NpgsqlCommand("""
            with v as (select model, to_char(started_at at time zone 'UTC', 'HH24') as h, 'd' as metric, duration_ms::float8 as ms from gateway.requests where status <> 'refused'
                       union all select model, to_char(started_at at time zone 'UTC', 'HH24'), 'f', extract(epoch from (first_token_at - started_at)) * 1000 from gateway.requests where first_token_at is not null and status <> 'refused'),
            s as (select model, to_char(hour at time zone 'UTC', 'HH24') as h, metric::text as metric, bucket, n from gateway.requests_hourly_spread where not refused),
            g as (select metric, k, p from (values ('d'), ('f')) m(metric) cross join (values ('all'), ('model'), ('hour')) w(k) cross join (values (0.5::float8), (0.95), (0.99)) q(p))
            select g.metric, g.k, g.p, x.grp, x.exact, y.sketched from g
            cross join lateral (select case g.k when 'model' then v.model when 'hour' then v.h else '' end as grp, percentile_disc(g.p) within group (order by v.ms) as exact
                                from v where v.metric = g.metric group by 1) x
            cross join lateral (select gateway.sketch_quantile(g.p, array_agg(s.bucket), array_agg(s.n)) as sketched from s
                                where s.metric = g.metric and (case g.k when 'model' then s.model when 'hour' then s.h else '' end) = x.grp) y
            """, conn);
        var checks = 0;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var (exact, sketched) = (r.GetDouble(4), r.GetDouble(5));
                Assert.True(Math.Abs(sketched - exact) <= 0.0101 * exact, $"{r.GetString(0)} by {r.GetString(1)} ({r.GetString(3)}), p{r.GetDouble(2) * 100}: {sketched} against {exact}");
                checks++;
            }
        }
        Assert.True(checks > 100, $"only {checks} quantiles checked");
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
            .SelectMany(d => d.Panels.SelectMany(p => p.Targets.Where(t => t.RawSql is { } q && (q.Contains(SqlMacros.Requests, StringComparison.Ordinal) || q.Contains(SqlMacros.Spread, StringComparison.Ordinal)))
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
                    checkedBoth += fast.Contains("from requests_hourly", StringComparison.Ordinal) ? 1 : 0;
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
        Assert.Equal(0, await SpreadDifferencesAsync(conn));
        // One at a time, as the gateway books them.
        await SeedAsync(conn, 1, offset: 3000);
        await SeedAsync(conn, 1, offset: 3001);
        Assert.Equal(0, await DifferencesAsync(conn));
        // Changed: a chat put in a project, a person attributed, tokens and cost corrected, a time moved across an hour.
        await RunAsync("update gateway.requests set project_id = '00000000-0000-4000-8000-000000000009' where person = 'p3@example.test'");
        await RunAsync("update gateway.requests set person = 'p1@example.test' where person is null and model = 'model-1'");
        await RunAsync("update gateway.requests set completion_tokens = completion_tokens + 5, cost = cost * 2 where status = 'failed'");
        await RunAsync("update gateway.requests set started_at = started_at + interval '45 minutes' where surface = 'media'");
        await RunAsync("update gateway.requests set first_token_at = started_at + interval '9 s', duration_ms = duration_ms * 3 where model = 'model-0'");
        Assert.Equal(0, await DifferencesAsync(conn));
        Assert.Equal(0, await SpreadDifferencesAsync(conn));
        // Deleted, as retention does; then all of it.
        await RunAsync("delete from gateway.requests where started_at < now() - interval '1 day' and model = 'model-3'");
        Assert.Equal(0, await DifferencesAsync(conn));
        await RunAsync("delete from gateway.requests where key_alias = 'key-2'");
        Assert.Equal(0, await DifferencesAsync(conn));
        Assert.Equal(0, await SpreadDifferencesAsync(conn));
        await RunAsync("truncate gateway.requests");
        await using var count = new NpgsqlCommand("select (select count(*) from gateway.requests_hourly) + (select count(*) from gateway.requests_hourly_spread)", conn);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }
    /// <summary>Stack Performance's "Tokens generated, by model" as it was, on the ledger's rows (its median by percentile_cont).</summary>
    private const string TokensByModelBefore = @"SELECT model AS ""Model"", count(*) AS ""Requests"", COALESCE(sum(completion_tokens),0) AS ""Tokens generated"", round(100.0*COALESCE(sum(completion_tokens),0)/NULLIF(sum(sum(completion_tokens)) OVER (),0),1) AS ""Share %"", round((COALESCE(sum(completion_tokens),0)::numeric / GREATEST($__range_s,1)),2) AS ""Tokens a second, the window"", round((SELECT max(m.t) FROM (SELECT sum(r2.completion_tokens)/60.0 AS t FROM requests r2 WHERE r2.model = requests.model AND $__timeFilter(r2.started_at) AND $__filter(r2.model, model) AND r2.status <> 'refused' GROUP BY date_trunc('minute', r2.started_at)) m)::numeric,1) AS ""Busiest minute, tokens a second"", round((sum(completion_tokens - 1) FILTER (WHERE (completion_tokens > 1 AND first_token_at IS NOT NULL AND ended_at > first_token_at)) / NULLIF(sum(extract(epoch FROM (ended_at - first_token_at))) FILTER (WHERE (completion_tokens > 1 AND first_token_at IS NOT NULL AND ended_at > first_token_at)),0))::numeric,1) AS ""Decode speed a request"", COALESCE(sum(greatest(prompt_tokens - cached_tokens,0)),0) AS ""Input, uncached"", COALESCE(sum(cached_tokens),0) AS ""Input, cached"", round(COALESCE(percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM (first_token_at - started_at))),0)::numeric,2) AS ""Median first token, s"" FROM requests WHERE $__timeFilter(started_at) AND $__filter(model, model) AND status <> 'refused' GROUP BY model ORDER BY ""Tokens generated"" DESC";

    [Fact]
    public async Task Tokens_generated_by_model_says_what_it_said_from_the_rows_its_median_within_one_percent()
    {
        var cs = app.ConnectionStringFor("rollupm_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway());
        _ = f.Server;
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await SeedAsync(conn, 6000);
        }
        var ledger = f.Services.GetRequiredService<GatewayDatasource>();
        var now = f.Services.GetRequiredService<DashboardStore>().All().Single(d => d.Uid == "stack-performance").Panels
            .Single(p => p.Title == "Tokens generated, by model").Targets.Single().RawSql!;
        var (from, to) = (End.AddDays(-30).AddMinutes(-17), End);
        var before = await ledger.QueryAsync(SqlMacros.Expand(TokensByModelBefore, from, to, TimeSpan.FromHours(2)), CancellationToken.None);
        var after = await ledger.QueryAsync(SqlMacros.Expand(now, from, to, TimeSpan.FromHours(2)), CancellationToken.None);
        Assert.Equal(before.Columns.Select(c => c.Name), after.Columns.Select(c => c.Name));
        Assert.Equal(before.Rows.Count, after.Rows.Count);
        var median = before.Columns.ToList().FindIndex(c => c.Name == "Median first token, s");
        for (var i = 0; i < before.Rows.Count; i++)
        {
            for (var c = 0; c < before.Columns.Count; c++)
            {
                if (c == median)
                {
                    var (x, y) = (Convert.ToDouble(before.Rows[i][c], CultureInfo.InvariantCulture), Convert.ToDouble(after.Rows[i][c], CultureInfo.InvariantCulture));
                    // percentile_cont interpolates, the sketch ranks as percentile_disc: 1% and the panel's rounding (0.01 s).
                    Assert.True(Math.Abs(x - y) <= 0.0101 * x + 0.011, $"row {i}: median {y} against {x}");
                }
                else
                {
                    Assert.Equal(Convert.ToString(before.Rows[i][c], CultureInfo.InvariantCulture), Convert.ToString(after.Rows[i][c], CultureInfo.InvariantCulture));
                }
            }
        }
    }
}
