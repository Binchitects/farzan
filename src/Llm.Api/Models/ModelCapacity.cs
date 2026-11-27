// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json.Nodes;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Models;

/// <summary>A model's capacity as its test bench measured it: the most tokens a second it made, and with how many answers at once.</summary>
public sealed record MeasuredCapacity(double TokensPerSecond, int AtOnce, DateTimeOffset At);

/// <summary>
/// Each model's capacity, from its latest finished capacity test (Admin → Models → Test): what the dashboards' capacity
/// is, measured rather than guessed from the busiest minute. Read again at most once a minute.
/// </summary>
public sealed class ModelCapacity(IServiceScopeFactory scopes, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _one = new(1, 1);
    private IReadOnlyDictionary<string, MeasuredCapacity> _known = new Dictionary<string, MeasuredCapacity>(StringComparer.Ordinal);
    private DateTimeOffset _read = DateTimeOffset.MinValue;

    public void Dispose() => _one.Dispose();

    public async Task<IReadOnlyDictionary<string, MeasuredCapacity>> GetAsync(CancellationToken ct = default)
    {
        if (clock.GetUtcNow() - _read < Fresh)
        {
            return _known;
        }
        await _one.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - _read < Fresh)
            {
                return _known;
            }
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var runs = await db.ModelBenchRuns.AsNoTracking().Where(r => r.Kind == ModelBenchRun.Capacity && r.Status == ModelBenchRun.Done)
                .OrderByDescending(r => r.FinishedAt).Select(r => new { r.Model, r.Results, r.FinishedAt }).Take(500).ToListAsync(ct);
            var known = new Dictionary<string, MeasuredCapacity>(StringComparer.Ordinal);
            foreach (var r in runs.Where(r => !string.IsNullOrEmpty(r.Model)))
            {
                if (known.ContainsKey(r.Model) || JsonNode.Parse(r.Results)?["best"] is not JsonObject best)
                {
                    continue;
                }
                var tps = best["tokensPerSecond"]?.GetValue<double>() ?? 0;
                if (tps > 0)
                {
                    known[r.Model] = new MeasuredCapacity(tps, best["atOnce"]?.GetValue<int>() ?? 1, r.FinishedAt ?? clock.GetUtcNow());
                }
            }
            (_known, _read) = (known, clock.GetUtcNow());
            return known;
        }
        finally
        {
            _one.Release();
        }
    }
}
