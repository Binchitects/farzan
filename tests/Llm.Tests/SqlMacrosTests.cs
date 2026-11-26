using Llm.Api.Dashboards;

namespace Llm.Tests;

public sealed class SqlMacrosTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 24, 12, 30, 5, TimeSpan.FromHours(3));

    [Fact]
    public void Time_filter_is_utc_rfc3339_like_grafana()
    {
        var sql = SqlMacros.Expand("""where $__timeFilter(s."startTime")""", From, To, TimeSpan.FromMinutes(5));
        Assert.Equal("""where s."startTime" BETWEEN '2026-09-01T00:00:00Z' AND '2026-09-24T09:30:05Z'""", sql);
    }

    [Fact]
    public void Time_group_alias_buckets_by_seconds()
    {
        Assert.Equal("""select floor(extract(epoch from s."startTime")/86400)*86400 AS "time", 1""",
            SqlMacros.Expand("""select $__timeGroupAlias(s."startTime", 1d), 1""", From, To, TimeSpan.FromMinutes(5)));
        Assert.Equal("""floor(extract(epoch from "startTime")/300)*300 AS "time" """.TrimEnd(),
            SqlMacros.Expand("""$__timeGroupAlias("startTime", $__interval)""", From, To, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Nested_parentheses_and_quotes_in_arguments_survive()
    {
        var sql = SqlMacros.Expand("$__timeFilter(coalesce(a.\"t,x\", date_trunc('day', b)))", From, To, TimeSpan.FromMinutes(1));
        Assert.StartsWith("coalesce(a.\"t,x\", date_trunc('day', b)) BETWEEN '2026-09-01T00:00:00Z'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Interval_variables_expand()
    {
        var to = From.AddDays(23); // exactly 23 days: 1,987,200 s
        Assert.Equal("300000 5m 1987200 23d", SqlMacros.Expand("$__interval_ms $__interval $__range_s $__range", From, to, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(1_000, 1_000)]
    [InlineData(40_000, 30_000)]
    [InlineData(100_000, 120_000)]
    [InlineData(4_000_000, 3_600_000)]
    [InlineData(6_480_000, 7_200_000)]   // 30 days over 400 points
    [InlineData(90_000_000, 86_400_000)]
    public void Intervals_round_like_grafana(long rawMs, long expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), SqlMacros.RoundInterval(TimeSpan.FromMilliseconds(rawMs)));

    [Theory]
    [InlineData("1d", 86400)]
    [InlineData("'5m'", 300)]
    [InlineData("30s", 30)]
    [InlineData("90", 90)]
    [InlineData("1w", 604800)]
    public void Intervals_parse(string text, double seconds) =>
        Assert.Equal(seconds, SqlMacros.ParseInterval(text).TotalSeconds);

    private static Variable ModelVariable() => new("model", "Model", "query", "gateway-db", null, true, true, ".*", null, 1, null, [], 0);

    [Fact]
    public void A_variables_choice_is_bound_as_a_parameter_and_never_written_into_the_sql()
    {
        var vars = new Dictionary<string, VariableValue> { ["model"] = new(ModelVariable(), ["qwen", "x'); drop table requests; --"], []) };
        var sql = SqlMacros.Expand("select count(*) from requests r where $__timeFilter(r.started_at) and $__filter(r.model, model)", From, To, TimeSpan.FromMinutes(1), vars, out var bound);
        Assert.EndsWith("and r.model = ANY(@v_model)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("drop table", sql, StringComparison.Ordinal);
        Assert.Equal(["qwen", "x'); drop table requests; --"], (string[])bound["v_model"]);
    }

    [Fact]
    public void All_or_no_such_variable_filters_nothing_and_a_variable_is_named_as_one()
    {
        var all = new Dictionary<string, VariableValue> { ["model"] = new(ModelVariable(), ["$__all"], ["qwen"]) };
        Assert.Equal("where TRUE", SqlMacros.Expand("where $__filter(model, model)", From, To, TimeSpan.FromMinutes(1), all, out var none));
        Assert.Empty(none);
        Assert.Equal("where TRUE", SqlMacros.Expand("where $__filter(model, other)", From, To, TimeSpan.FromMinutes(1), all, out _));
        Assert.Throws<FormatException>(() => SqlMacros.Expand("where $__filter(model, 1=1)", From, To, TimeSpan.FromMinutes(1), all, out _));
    }
    [Fact]
    public void The_ledger_as_panels_sum_it_reads_whole_hours_from_the_rollup_and_the_minutes_either_side_from_the_rows()
    {
        var from = new DateTimeOffset(2026, 11, 1, 10, 17, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 3, 8, 5, 30, TimeSpan.Zero);
        var sql = SqlMacros.Expand("select sum(s.requests) from $__requests s where $__timeFilter(s.started_at)", from, to, TimeSpan.FromHours(1));
        Assert.Contains("from requests_hourly where hour >= '2026-11-01T11:00:00Z' and hour < '2026-11-03T08:00:00Z'", sql, StringComparison.Ordinal);
        Assert.Contains("(started_at >= '2026-11-01T10:17:00Z' and started_at < '2026-11-01T11:00:00Z') or (started_at >= '2026-11-03T08:00:00Z' and started_at <= '2026-11-03T08:05:30Z')", sql, StringComparison.Ordinal);
        // Buckets of half an hour, or a window with no whole hour in it: the rows alone; a panel with no buckets uses the rollup.
        Assert.DoesNotContain("requests_hourly", SqlMacros.Expand("select $__timeGroup(s.started_at, $__interval), sum(s.requests) from $__requests s", from, to, TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
        Assert.Contains("requests_hourly", SqlMacros.Expand("select sum(s.requests) from $__requests s", from, to, TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
        Assert.DoesNotContain("requests_hourly", SqlMacros.Expand("select sum(s.requests) from $__requests s", from, from.AddMinutes(50), TimeSpan.FromHours(1)), StringComparison.Ordinal);
        Assert.DoesNotContain("requests_hourly", SqlMacros.Expand("select sum(s.requests) from $__requests s", from, to, TimeSpan.FromHours(1), null, out _, rollup: false), StringComparison.Ordinal);
    }
}
