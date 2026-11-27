// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Dashboards;

/// <summary>
/// Grafana's PostgreSQL macros, expanded the way Grafana expands them, so a
/// panel's SQL gives the same rows here as in Grafana.
/// </summary>
public static partial class SqlMacros
{
    [GeneratedRegex(@"\$__(timeFilter|timeGroupAlias|timeGroup|timeFrom|timeTo|unixEpochFilter|filter)\s*\(")]
    private static partial Regex MacroStart();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex VariableName();

    public static string Expand(string sql, DateTimeOffset from, DateTimeOffset to, TimeSpan interval) => Expand(sql, from, to, interval, null, out _);

    /// <summary>
    /// The macros, and the app's own $__filter(column, variable): the column among the variable's chosen values, bound as a
    /// parameter (@v_variable, in <paramref name="parameters"/>), or TRUE for All or no such variable. A value is never
    /// written into the SQL.
    /// </summary>
    public static string Expand(string sql, DateTimeOffset from, DateTimeOffset to, TimeSpan interval, IReadOnlyDictionary<string, VariableValue>? vars,
        out Dictionary<string, object> parameters, bool rollup = true)
    {
        parameters = [];
        // The ledger's rows as a panel sums them, before $__interval is read: whether its buckets are whole hours.
        var hourly = rollup && (!sql.Contains("$__interval", StringComparison.Ordinal) || (interval >= TimeSpan.FromHours(1) && interval.Ticks % TimeSpan.TicksPerHour == 0));
        if (sql.Contains(Requests, StringComparison.Ordinal))
        {
            sql = sql.Replace(Requests, hourly ? Ledger(from, to) : $"(select {RawColumns} from requests)", StringComparison.Ordinal);
        }
        if (sql.Contains(Spread, StringComparison.Ordinal))
        {
            sql = sql.Replace(Spread, hourly ? SpreadOf(from, to) : $"({RawSpread("true")})", StringComparison.Ordinal);
        }
        // Plain variables first: $__interval can be an argument of a macro.
        sql = sql
            .Replace("$__interval_ms", ((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$__interval", FormatInterval(interval), StringComparison.Ordinal)
            .Replace("$__range_s", ((long)(to - from).TotalSeconds).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$__range", FormatInterval(to - from), StringComparison.Ordinal);

        var sb = new StringBuilder();
        var pos = 0;
        while (MacroStart().Match(sql, pos) is { Success: true } m)
        {
            sb.Append(sql, pos, m.Index - pos);
            var close = FindClose(sql, m.Index + m.Length);
            var args = SplitArgs(sql[(m.Index + m.Length)..close]);
            sb.Append(m.Groups[1].Value switch
            {
                "timeFilter" => $"{Arg(args, 0)} BETWEEN '{Rfc3339(from)}' AND '{Rfc3339(to)}'",
                "timeFrom" => $"'{Rfc3339(from)}'",
                "timeTo" => $"'{Rfc3339(to)}'",
                "unixEpochFilter" => $"{Arg(args, 0)} >= {from.ToUnixTimeSeconds()} AND {Arg(args, 0)} <= {to.ToUnixTimeSeconds()}",
                "timeGroup" => TimeGroup(args),
                "timeGroupAlias" => TimeGroup(args) + " AS \"time\"",
                "filter" => Filter(args, vars, parameters),
                _ => throw new InvalidOperationException(m.Value),
            });
            pos = close + 1;
        }
        sb.Append(sql, pos, sql.Length - pos);
        return sb.ToString();
    }

    /// <summary>The ledger as a panel sums it: <c>from $__requests s</c>.</summary>
    public const string Requests = "$__requests";

    /// <summary>The columns $__requests has: the ledger's own, and how many requests each row is.</summary>
    public const string RequestColumns = "started_at, person, model, surface, credit_kind, key_alias, status, project_id, requests, prompt_tokens, cached_tokens, completion_tokens, total_tokens, cost, duration_ms";

    private const string RawColumns = "started_at, person, model, surface, credit_kind, key_alias, status, project_id, 1::bigint as requests, prompt_tokens, cached_tokens, completion_tokens, total_tokens, cost, duration_ms";

    /// <summary>
    /// $__requests: the ledger's rows for sums and counts (count(*) is sum(requests)). Over a window with whole hours
    /// in it, those hours from gateway.requests_hourly (an hour's row at its start) and the minutes either side from the
    /// rows themselves, so every sum is the rows' own; the rows alone when the panel's buckets ($__interval) are not
    /// whole hours, or <paramref name="rollup"/> is off.
    /// </summary>
    /// <summary>The ledger's spread as panels read percentiles from it: <c>from $__spread s</c> (gateway.sketch_quantile over its buckets).</summary>
    public const string Spread = "$__spread";

    /// <summary>
    /// The rows' own spread where <paramref name="where"/> holds: each request's time ("d", ms) and its time to the first
    /// token ("f", ms, when it had one), in the sketch's buckets, a count of 1 each.
    /// </summary>
    private static string RawSpread(string where) =>
        "select started_at, model, status = 'refused' as refused, surface = 'media' as media, 'd'::char(1) as metric, sketch_bucket(duration_ms::float8) as bucket, 1::bigint as n "
        + $"from requests where {where} union all select started_at, model, status = 'refused', surface = 'media', 'f'::char(1), "
        + $"sketch_bucket(extract(epoch from (first_token_at - started_at)) * 1000), 1::bigint from requests where first_token_at is not null and ({where})";

    /// <summary>
    /// $__spread over a window (columns started_at, model, refused, media, metric, bucket, n): whole hours from
    /// gateway.requests_hourly_spread, the minutes either side from the rows in the same buckets, so a percentile is the
    /// same as from the rows alone (within 1% of the requests' own either way).
    /// </summary>
    private static string SpreadOf(DateTimeOffset from, DateTimeOffset to)
    {
        if (WholeHours(from, to) is not { } hours)
        {
            return $"({RawSpread("true")})";
        }
        var (start, end) = hours;
        var (f, t, a, b) = (Rfc3339(from), Rfc3339(to), Rfc3339(start), Rfc3339(end));
        return $"(select hour as started_at, model, refused, media, metric, bucket, n from requests_hourly_spread where hour >= '{a}' and hour < '{b}' "
            + $"union all {RawSpread($"(started_at >= '{f}' and started_at < '{a}') or (started_at >= '{b}' and started_at <= '{t}')")})";
    }

    /// <summary>The whole hours of a window (the first at or after its start, the end at or before its end); null with none.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End)? WholeHours(DateTimeOffset from, DateTimeOffset to)
    {
        var start = new DateTimeOffset(from.UtcTicks - from.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        if (start < from)
        {
            start = start.AddHours(1);
        }
        var end = new DateTimeOffset(to.UtcTicks - to.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        return end > start ? (start, end) : null;
    }

    /// <summary>
    /// The ledger's rows from <paramref name="from"/> to <paramref name="to"/> as sums and counts read them (its columns:
    /// <see cref="RequestColumns"/>; count(*) is sum(requests)): the whole hours from gateway.requests_hourly (an hour's
    /// row at its start), the minutes either side from the rows themselves; the rows alone with no whole hour between.
    /// The caller still filters by the window, its end in or out. <paramref name="schema"/>: "gateway." outside the
    /// dashboards' own search path.
    /// </summary>
    public static string Ledger(DateTimeOffset from, DateTimeOffset to, string schema = "")
    {
        var raw = $"(select {RawColumns} from {schema}requests)";
        if (WholeHours(from, to) is not { } hours)
        {
            return raw;
        }
        var (start, end) = hours;
        var (f, t, a, b) = (Rfc3339(from), Rfc3339(to), Rfc3339(start), Rfc3339(end));
        return $"(select hour as started_at, person, model, surface, credit_kind, key_alias, status, project_id, requests, prompt_tokens, cached_tokens, "
            + $"completion_tokens, total_tokens, cost, duration_ms from {schema}requests_hourly where hour >= '{a}' and hour < '{b}' "
            + $"union all select {RawColumns} from {schema}requests where (started_at >= '{f}' and started_at < '{a}') or (started_at >= '{b}' and started_at <= '{t}'))";
    }

    private static string Filter(List<string> args, IReadOnlyDictionary<string, VariableValue>? vars, Dictionary<string, object> parameters)
    {
        var column = Arg(args, 0);
        var name = Arg(args, 1).Trim();
        if (!VariableName().IsMatch(name))
        {
            throw new FormatException($"$__filter names a variable, not \"{name}\".");
        }
        if (vars?.GetValueOrDefault(name) is not { } v || v.Values.Contains("$__all") || v.Values.Count == 0)
        {
            return "TRUE";
        }
        parameters["v_" + name] = v.Values.ToArray();
        return $"{column} = ANY(@v_{name})";
    }

    /// <summary>Grafana: floor(extract(epoch from col)/secs)*secs.</summary>
    private static string TimeGroup(List<string> args)
    {
        var secs = ParseInterval(Arg(args, 1)).TotalSeconds;
        var s = secs.ToString("0.###", CultureInfo.InvariantCulture);
        return $"floor(extract(epoch from {Arg(args, 0)})/{s})*{s}";
    }

    private static string Arg(List<string> args, int i) =>
        i < args.Count ? args[i] : throw new FormatException($"A macro is missing argument {i + 1}.");

    /// <summary>Grafana writes times in UTC, RFC 3339 with nanoseconds trimmed.</summary>
    public static string Rfc3339(DateTimeOffset t) =>
        t.UtcDateTime.ToString(t.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static int FindClose(string s, int start)
    {
        var depth = 1;
        var quote = '\0';
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return i;
            }
        }
        throw new FormatException("A macro's parenthesis is never closed.");
    }

    private static List<string> SplitArgs(string s)
    {
        var args = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        args.Add(s[start..].Trim());
        return args;
    }

    /// <summary>"1d", "5m", "30s", "250ms", "2h", "1w" (and a bare number of seconds).</summary>
    public static TimeSpan ParseInterval(string text)
    {
        text = text.Trim().Trim('\'', '"');
        var m = IntervalPattern().Match(text);
        if (!m.Success)
        {
            throw new FormatException($"Not an interval: {text}");
        }
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "" or "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            "w" => TimeSpan.FromDays(7 * n),
            "y" => TimeSpan.FromDays(365 * n),
            _ => throw new FormatException(text),
        };
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(ms|s|m|h|d|w|y)?$")]
    private static partial Regex IntervalPattern();

    /// <summary>The largest whole unit, as Grafana prints $__interval ("5m", "1h", "1d").</summary>
    public static string FormatInterval(TimeSpan t)
    {
        var ms = (long)t.TotalMilliseconds;
        foreach (var (unit, size) in new (string, long)[] { ("d", 86_400_000), ("h", 3_600_000), ("m", 60_000), ("s", 1000) })
        {
            if (ms >= size && ms % size == 0)
            {
                return (ms / size).ToString(CultureInfo.InvariantCulture) + unit;
            }
        }
        return ms.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    /// <summary>
    /// Grafana's interval rounding (rangeutil.roundInterval): the bucket a panel
    /// asks for is (range / points) rounded to a "nice" size from this table.
    /// </summary>
    public static TimeSpan RoundInterval(TimeSpan raw)
    {
        var i = raw.TotalMilliseconds;
        long ms = i switch
        {
            < 15 => 10, < 35 => 20, < 75 => 50, < 150 => 100, < 350 => 200, < 750 => 500,
            < 1500 => 1000, < 3500 => 2000, < 7500 => 5000, < 12500 => 10000, < 17500 => 15000,
            < 25000 => 20000, < 45000 => 30000, < 90000 => 60000, < 210000 => 120000,
            < 450000 => 300000, < 750000 => 600000, < 1050000 => 900000, < 1500000 => 1200000,
            < 2700000 => 1800000, < 5400000 => 3600000, < 9000000 => 7200000, < 16200000 => 10800000,
            < 32400000 => 21600000, < 86400000 => 43200000, < 604800000 => 86400000,
            < 1814400000 => 604800000, < 3628800000 => 2592000000, _ => 31536000000,
        };
        return TimeSpan.FromMilliseconds(ms);
    }
}
