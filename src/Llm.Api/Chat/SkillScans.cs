// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>Configuration section "Skills": SkillSpector's checks of the skills (the scanners feature).</summary>
public sealed class SkillScanOptions
{
    /// <summary>The scanners' address (the scanners feature); empty: skills are not checked.</summary>
    public string ScannerUrl { get; set; } = "";

    /// <summary>A skill SkillSpector says not to install is refused on import (else imported, and kept from use until an admin lets it through).</summary>
    public bool RefuseUnsafe { get; set; } = true;

    /// <summary>How often skills made or changed since their last check are looked for.</summary>
    public TimeSpan ScanEvery { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>What SkillSpector said of a skill: its risk (0 to 100), its word (SAFE, CAUTION, DO_NOT_INSTALL) and its report.</summary>
public sealed record SkillScan(int? Score, string? Recommendation, string Report);

/// <summary>
/// Each skill checked by SkillSpector (in the scanners image) as it is made, imported, learned or changed, and again when what
/// it says changes: its SKILL.md and files sent as a skill folder, static checks only. One found unsafe (DO_NOT_INSTALL) is
/// kept from use (not offered to the model, nor used by its name) until an admin lets it through; on import it is refused
/// (Skills:RefuseUnsafe). Without the scanners, skills are not checked, and say so.
/// </summary>
public sealed partial class SkillScans(IHttpClientFactory http, IOptionsMonitor<SkillScanOptions> options, IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<SkillScans> logger) : BackgroundService
{
    public const string Client = "skill-scans";

    /// <summary>The most skills one look checks.</summary>
    private const int PerLook = 20;

    public bool On => options.CurrentValue.ScannerUrl is { Length: > 0 };

    /// <summary>The print of what a skill says: a skill whose print is not its last check's is checked again.</summary>
    public static string Hash(Skill s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\u0001', s.Name, s.Title, s.Description, s.Instructions, s.Parameters, s.Files))));

    /// <summary>A skill as its folder: SKILL.md (its name and description in its front matter, its instructions), and its files.</summary>
    public static byte[] Folder(Skill s)
    {
        using var buffer = new MemoryStream();
        using (var tar = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
                tar.WriteEntry(entry);
            }
            Add("SKILL.md", $"---\nname: {s.Name}\ndescription: {JsonSerializer.Serialize(s.Description)}\n---\n\n# {s.Title}\n\n{s.Instructions}\n");
            foreach (var (path, text) in JsonSerializer.Deserialize<Dictionary<string, string>>(s.Files) ?? [])
            {
                var safe = path.Replace('\\', '/').TrimStart('/');
                if (safe.Length > 0 && !safe.Split('/').Contains("..") && !string.Equals(safe, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    Add(safe, text);
                }
            }
        }
        return buffer.ToArray();
    }

    /// <summary>A skill checked now: what SkillSpector said, or null when the scanners are not there (the feature off) or did not answer.</summary>
    public async Task<SkillScan?> ScanAsync(Skill skill, CancellationToken ct)
    {
        if (!On)
        {
            return null;
        }
        try
        {
            using var content = new ByteArrayContent(Folder(skill));
            content.Headers.ContentType = new("application/x-tar");
            using var res = await http.CreateClient(Client).PostAsync(new Uri(options.CurrentValue.ScannerUrl.TrimEnd('/') + "/skill"), content, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                LogFailed(logger, skill.Name, $"HTTP {(int)res.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
                return null;
            }
            var report = JsonNode.Parse(text) as JsonObject ?? [];
            int? score = report["score"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : report["score"] is JsonValue d && d.TryGetValue<double>(out var x) ? (int)Math.Round(x) : null;
            return new SkillScan(score, report["recommendation"]?.ToString(), report.ToJsonString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogFailed(logger, skill.Name, ex.Message);
            return null;
        }
    }

    /// <summary>A check kept on the skill (tracked by the caller's context): a skill that changed asks to be let through again.</summary>
    public void Keep(Skill skill, SkillScan scan)
    {
        var hash = Hash(skill);
        if (skill.ScanHash != hash)
        {
            skill.ScanAllowedById = null;
        }
        (skill.ScanHash, skill.ScanScore, skill.ScanRecommendation, skill.ScanReport, skill.ScannedAt) = (hash, scan.Score, scan.Recommendation, scan.Report, clock.GetUtcNow());
    }

    /// <summary>The skills made or changed since their last check, checked, a few at a time.</summary>
    public async Task<int> LookAsync(CancellationToken ct)
    {
        if (!On)
        {
            return 0;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checkedNow = 0;
        // Those never checked first, then the oldest checks (whose skill may have changed since).
        foreach (var skill in await db.Skills.OrderBy(s => s.ScannedAt != null).ThenBy(s => s.ScannedAt).ThenBy(s => s.Id).ToListAsync(ct))
        {
            if (checkedNow >= PerLook || ct.IsCancellationRequested)
            {
                break;
            }
            if (skill.ScanHash == Hash(skill))
            {
                continue;
            }
            if (await ScanAsync(skill, ct) is not { } scan)
            {
                break;
            }
            Keep(skill, scan);
            await db.SaveChangesAsync(ct);
            checkedNow++;
        }
        return checkedNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.CurrentValue.ScanEvery, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await LookAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                LogFailed(logger, "(the look)", ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skill {Skill} could not be checked by SkillSpector: {Reason}")]
    private static partial void LogFailed(ILogger logger, string skill, string reason);
}

/// <summary>What a SkillSpector report says, in words and as the Skills page shows it.</summary>
public static class SkillScanWords
{
    private static readonly string[] WordFields = ["title", "message", "explanation", "description", "name", "category", "pattern_id", "id"];

    /// <summary>An issue's words, from whichever of SkillSpector's fields it has.</summary>
    private static string Words(JsonNode? issue) =>
        WordFields
            .Select(k => issue?[k] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "an issue";

    /// <summary>The most severe issue's words (the scanners send them most severe first).</summary>
    public static string FirstIssue(string report)
    {
        var issues = (JsonNode.Parse(report)?["issues"] as JsonArray) ?? [];
        return issues.Count == 0 ? "no issue named" : Words(issues[0]) + (issues.Count > 1 ? $" (and {issues.Count - 1} more)" : "");
    }

    /// <summary>A skill's check as the Skills page shows it: null when never checked; stale when it changed since.</summary>
    public static object? Shown(Skill s)
    {
        if (s.ScannedAt is null || s.ScanReport is null)
        {
            return null;
        }
        var report = JsonNode.Parse(s.ScanReport) as JsonObject ?? [];
        var issues = (report["issues"] as JsonArray ?? []).Take(20).Select(i => new
        {
            severity = i?["severity"]?.ToString(), title = Words(i), where = (i?["location"] ?? i?["file"] ?? i?["path"])?.ToString(),
        });
        return new
        {
            score = s.ScanScore, recommendation = s.ScanRecommendation, severity = report["severity"]?.ToString(), version = report["version"]?.ToString(),
            issues, more = Math.Max(0, (report["issues"] as JsonArray)?.Count - 20 ?? 0) + (report["more"]?.GetValue<int>() ?? 0),
            at = s.ScannedAt, allowed = s.ScanAllowedById is not null, blocked = s.Blocked, stale = s.ScanHash != SkillScans.Hash(s),
        };
    }
}
