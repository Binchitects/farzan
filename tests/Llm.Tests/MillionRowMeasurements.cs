using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>
/// A measurement, run only when ARENA_MEASURE_MILLION=1: <c>tools/dn test tests/Llm.Tests --filter MillionRow -e ARENA_MEASURE_MILLION=1</c>
/// (ARENA_MEASURE_OUT=FILE writes the times as JSON too). It is minutes of seeding, so not in the suite.
/// </summary>
public sealed class MillionRowFactAttribute : FactAttribute
{
    public MillionRowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ARENA_MEASURE_MILLION") != "1")
        {
            Skip = "A measurement of minutes: set ARENA_MEASURE_MILLION=1 to run it.";
        }
    }
}

/// <summary>
/// A million requests in the ledger and a million lines in the prompt log, over 30 days, then each export of every
/// dashboard, Accounting (whole and narrowed), the usage exports and the prompt log's searches and filters timed, each
/// asked twice (cold, then warm). Each must answer in under 2 seconds warm.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class MillionRowMeasurements(AppFixture app, ITestOutputHelper output)
{
    public const int Rows = 1_000_000;

    /// <summary>Under this, warm, each answer.</summary>
    public static readonly TimeSpan Most = TimeSpan.FromSeconds(2);

    /// <summary>The rows end now (the hour begun), so the prompt log's own window (its newest) has them too.</summary>
    private static readonly DateTimeOffset End = new(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day, DateTimeOffset.UtcNow.Hour, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [MillionRowFact]
    public async Task Every_export_report_and_search_answers_in_under_two_seconds_on_a_million_rows()
    {
        var db = "million_" + Guid.NewGuid().ToString("N")[..8];
        await using var f = app.Create(app.ConnectionStringFor(db), new FakeGateway(), new Dictionary<string, string?>
        {
            // No Prometheus nor Loki: their panels answer at once with nothing, the ledger's are what is measured.
            ["Stack:PrometheusUrl"] = "http://127.0.0.1:9", ["Stack:LokiUrl"] = "http://127.0.0.1:9",
        }, fresh: true);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // Twenty people with their own accounts (the prompt log's lines are theirs); the ledger's 200 people include them.
        var people = new List<(Guid Id, string Email)>();
        for (var i = 0; i < 20; i++)
        {
            var name = $"m{i:00}" + Guid.NewGuid().ToString("N")[..6];
            var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
            people.Add((made.GetProperty("id").GetGuid(), $"{name}@example.test"));
        }
        var seed = Stopwatch.StartNew();
        await SeedAsync(app.ConnectionStringFor(db), people);
        output.WriteLine($"seeded {Rows:N0} ledger requests and {Rows:N0} prompt log lines in {seed.Elapsed.TotalSeconds:N0} s");

        var from = Uri.EscapeDataString(End.AddDays(-30).ToString("O"));
        var to = Uri.EscapeDataString(End.ToString("O"));
        var window = $"from={from}&to={to}";
        var asks = new List<(string Name, string Path)>
        {
            ("Accounting, 30 days", $"/api/admin/accounting?{window}"),
            ("Accounting, by tokens", $"/api/admin/accounting?{window}&basis=tokens"),
            ("Accounting, one model", $"/api/admin/accounting?{window}&model=model-2"),
            ("Accounting, one person", $"/api/admin/accounting?{window}&person={Uri.EscapeDataString(people[3].Email)}"),
            ("Accounting, CSV", $"/api/admin/accounting/export?{window}&format=csv"),
            ("Accounting, Excel", $"/api/admin/accounting/export?{window}&format=xlsx"),
            ("Accounting, PDF", $"/api/admin/accounting/export?{window}&format=pdf"),
            ("Usage by person, CSV", $"/api/admin/usage/export?{window}&format=csv"),
            ("Usage by person, one model, Excel", $"/api/admin/usage/export?{window}&format=xlsx&model=model-1"),
            ("Prompt log, newest", "/api/admin/prompts?source=api"),
            ("Prompt log, words", "/api/admin/prompts?source=api&q=" + Uri.EscapeDataString("kubernetes ingress")),
            ("Prompt log, a model", "/api/admin/prompts?source=api&model=model-3"),
            ("Prompt log, a tool called", "/api/admin/prompts?source=api&tool=read_file"),
            ("Prompt log, a person", $"/api/admin/prompts?source=api&person={Uri.EscapeDataString(people[5].Email)}"),
            ("Prompt log, failed", "/api/admin/prompts?source=api&state=failed"),
            ("Prompt log, words and a model, 30 days", $"/api/admin/prompts?source=api&{window}&model=model-0&q=postgres"),
        };
        foreach (var d in (await admin.JsonAsync(await admin.GetAsync("/api/dashboards"))).EnumerateArray())
        {
            var uid = d.GetProperty("uid").GetString()!;
            asks.Add(($"Dashboard {uid}, CSV", $"/api/dashboards/{uid}/export?{window}&format=csv"));
            asks.Add(($"Dashboard {uid}, PDF", $"/api/dashboards/{uid}/export?{window}&format=pdf"));
        }
        var times = new List<object>();
        var slow = new List<string>();
        foreach (var (name, path) in asks)
        {
            var cold = await TimeAsync(admin, path);
            var warm = await TimeAsync(admin, path);
            output.WriteLine($"{name}: {cold.TotalMilliseconds:N0} ms cold, {warm.TotalMilliseconds:N0} ms warm");
            times.Add(new { name, path, coldMs = Math.Round(cold.TotalMilliseconds), warmMs = Math.Round(warm.TotalMilliseconds) });
            if (warm > Most)
            {
                slow.Add($"{name} ({warm.TotalMilliseconds:N0} ms)");
            }
        }
        // A dashboard whose export took more than a second: each of its panels timed as the export asks it (400 points), the slowest first.
        var panels = new List<object>();
        foreach (var d in (await admin.JsonAsync(await admin.GetAsync("/api/dashboards"))).EnumerateArray().Select(d => d.GetProperty("uid").GetString()!))
        {
            if (!times.Cast<dynamic>().Any(t => ((string)t.name).StartsWith($"Dashboard {d},", StringComparison.Ordinal) && (double)t.warmMs > 1000))
            {
                continue;
            }
            var def = await admin.JsonAsync(await admin.GetAsync($"/api/dashboards/{d}"));
            var each = new List<(string Title, double Ms)>();
            foreach (var panel in def.GetProperty("panels").EnumerateArray().Where(x => x.GetProperty("type").GetString() is not ("row" or "text")))
            {
                var sw = Stopwatch.StartNew();
                var res = await admin.PostAsync($"/api/dashboards/{d}/panels/{panel.GetProperty("key").GetInt32()}/query",
                    new { from = End.AddDays(-30), to = End, maxDataPoints = 400 });
                await res.Content.ReadAsByteArrayAsync();
                each.Add((panel.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "", sw.Elapsed.TotalMilliseconds));
            }
            foreach (var (title, ms) in each.OrderByDescending(e => e.Ms).Take(6))
            {
                output.WriteLine($"  {d} / {title}: {ms:N0} ms");
                panels.Add(new { dashboard = d, title, ms = Math.Round(ms) });
            }
        }
        if (Environment.GetEnvironmentVariable("ARENA_MEASURE_OUT") is { Length: > 0 } file)
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { rows = Rows, seededSeconds = Math.Round(seed.Elapsed.TotalSeconds), times, slowestPanels = panels }, Indented));
        }
        Assert.True(slow.Count == 0, "Past 2 s warm: " + string.Join("; ", slow));
    }

    private static async Task<TimeSpan> TimeAsync(TestBrowser b, string path)
    {
        var sw = Stopwatch.StartNew();
        var res = await b.GetAsync(path);
        await res.Content.ReadAsByteArrayAsync();
        sw.Stop();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return sw.Elapsed;
    }

    /// <summary>
    /// The rows, made in the database itself: requests spread over 30 days by 200 people, each with their own source, kind and
    /// project and one or two of 6 models, as people work;
    /// prompt log lines of the 20 people, their questions from a few hundred words (for the trigram search), a tenth calling tools.
    /// </summary>
    private static async Task SeedAsync(string connection, List<(Guid Id, string Email)> people)
    {
        await using var conn = new Npgsql.NpgsqlConnection(connection);
        await conn.OpenAsync();
        async Task RunAsync(string sql, Action<Npgsql.NpgsqlCommand>? bind = null)
        {
            await using var cmd = new Npgsql.NpgsqlCommand(sql, conn) { CommandTimeout = 1200 };
            bind?.Invoke(cmd);
            await cmd.ExecuteNonQueryAsync();
        }
        var emails = people.Select(p => p.Email).ToArray();
        await RunAsync($"""
            insert into gateway.requests (id, started_at, first_token_at, ended_at, duration_ms, surface, call, protocol, credit_kind, person, model,
              prompt_tokens, cached_tokens, completion_tokens, usage_estimated, cost, status, http_status, project_id)
            select gen_random_uuid(), s.at, s.at + interval '200 ms', s.at + (s.ms || ' ms')::interval, s.ms,
                   (array['chat','api','code','studio'])[1 + s.p % 4], 'chat', 'openai', (array['chat','api','pictures'])[1 + s.p % 3],
                   case when s.p < 20 then (@emails)[1 + s.p] else 'person' || s.p || '@example.test' end,
                   'model-' || (s.p % 6 + s.i % 2), 500 + s.i % 4000, (s.i % 3) * 200, 50 + s.i % 900, false, ((s.i % 1000) / 100000.0)::numeric,
                   case when s.i % 50 = 0 then 'failed' else 'ok' end, case when s.i % 50 = 0 then 500 else 200 end,
                   case when s.p % 3 = 0 then ('00000000-0000-4000-8000-' || lpad((s.p % 50)::text, 12, '0'))::uuid end
            from (select g as i, ((g::bigint * 7919) % 200)::int as p, @end - (g * (30 * 86400.0 / {Rows}) || ' seconds')::interval as at, 300 + g % 9000 as ms
                  from generate_series(1, {Rows}) g) s
            """, c =>
        {
            c.Parameters.AddWithValue("emails", emails);
            c.Parameters.AddWithValue("end", End.UtcDateTime);
        });
        var ids = people.Select(p => p.Id).ToArray();
        await RunAsync($"""
            insert into prompt_log ("Id", "UserId", "Model", "Prompt", "Answer", "PromptTokens", "CompletionTokens", "Status", "At", "DurationMs", "State", "Tags", "Thread")
            select gen_random_uuid(), (@ids)[1 + g % {ids.Length}], 'model-' || (g % 6),
                   (array['how do I','fix the','why does','explain the','write a test for','review this'])[1 + g % 6] || ' '
                     || (array['kubernetes ingress','postgres index','react hook','dotnet build','python import','nginx config','grafana panel','docker compose'])[1 + (g / 7) % 8]
                     || ' ' || (g % 997)::text,
                   case when g % 10 = 0 then 'Called read_file, list_dir' else 'An answer about it, number ' || g end,
                   500 + g % 4000, 50 + g % 900, case when g % 40 = 0 then 500 else 200 end,
                   @end - (g * (30 * 86400.0 / {Rows}) || ' seconds')::interval, 300 + g % 9000,
                   case when g % 40 = 0 then 'failed' else 'done' end, array[]::text[], substr(md5((g % 5000)::text), 1, 16)
            from generate_series(1, {Rows}) g
            """, c =>
        {
            c.Parameters.AddWithValue("ids", ids);
            c.Parameters.AddWithValue("end", End.UtcDateTime);
        });
        // As autovacuum leaves a table that has settled: its pages all visible, so an index that covers a query is read alone.
        await RunAsync("vacuum analyze gateway.requests");
        await RunAsync("vacuum analyze prompt_log");
    }
}
