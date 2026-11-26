using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Schedules;
using Llm.Api.Settings;
using Llm.Core.Data;
using Llm.Core.Studio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Studio;

/// <summary>
/// What saving a workflow checks beyond its shape, as scheduled tasks did before they became Automations: a schedule only
/// while schedules are on here (Schedules:Enabled), no more often than Schedules:MinInterval, and at most Schedules:PerPerson
/// scheduled workflows a person; each webhook step's address an allowed host's (Schedules:WebhookHosts), sealed with
/// APP_DATA_KEY (such an address is a secret) and shown only as set.
/// </summary>
public sealed class FlowRules(IOptionsMonitor<ScheduleOptions> schedules, IOptions<AuthOptions> auth, Webhooks webhooks, TimeProvider clock)
{
    public ScheduleOptions Schedules => schedules.CurrentValue;

    /// <summary>Why this flow may not be saved for this workflow (its old flow kept the sealed addresses), or null; its webhook addresses sealed.</summary>
    public async Task<IResult?> ApplyAsync(Flow flow, Workflow w, AppDbContext db, CancellationToken ct)
    {
        var o = schedules.CurrentValue;
        var t = flow.Trigger;
        if (t.Kind == "schedule")
        {
            if (!o.Enabled)
            {
                return AuthEndpoints.Problem(403, "schedules", "Schedules are off here (Settings → Automations).");
            }
            var (cron, _) = Cron.Parse(t.Cron);
            var (zone, _) = Models.Hours.Zone(t.TimeZone);
            if (cron?.ShortestGap(clock.GetUtcNow(), zone) is { } gap && gap < o.MinInterval)
            {
                return AuthEndpoints.Problem(400, "cron", $"It would run every {Describe(gap)}: at most once every {Describe(o.MinInterval)} here.");
            }
            if (await db.Workflows.CountAsync(x => x.OwnerId == w.OwnerId && x.Id != w.Id && x.NextRunAt != null, ct) >= o.PerPerson)
            {
                return AuthEndpoints.Problem(409, "limit", $"You have {o.PerPerson} workflows on a schedule, as many as one person may. Remove one first.");
            }
        }
        var before = FlowModel.All(FlowModel.Read(w.Flow).Steps).Where(s => s.Kind == "webhook" && s.Secret is not null).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Secret);
        foreach (var s in FlowModel.All(flow.Steps).Where(s => s.Kind == "webhook"))
        {
            if (s.Url?.Trim() is { Length: > 0 } url)
            {
                if (webhooks.Refusal(url) is { } refused)
                {
                    return AuthEndpoints.Problem(400, "webhook", refused);
                }
                if (string.IsNullOrEmpty(auth.Value.DataKey))
                {
                    return AuthEndpoints.Problem(400, "data_key", "APP_DATA_KEY is not set, so a webhook's address (a secret) cannot be kept encrypted.");
                }
                s.Secret = SettingsCrypto.Encrypt(url, auth.Value.DataKey);
                s.Url = null;
            }
            else if (s.Secret == FlowModel.Sealed && before.GetValueOrDefault(s.Id) is { } kept)
            {
                s.Secret = kept;
            }
            else
            {
                return AuthEndpoints.Problem(400, "webhook", $"Step {s.Name ?? s.Id}: it needs the address to post to.");
            }
        }
        return null;
    }

    /// <summary>A webhook step's address, unsealed; null when APP_DATA_KEY changed since.</summary>
    public string? Address(FlowStep step) => step.Secret is { } sealedUrl ? SettingsCrypto.Decrypt(sealedUrl, auth.Value.DataKey) : null;

    private static string Describe(TimeSpan span) => span.TotalHours >= 1 && span.TotalMinutes % 60 == 0
        ? $"{span.TotalHours:0} hour{(span.TotalHours == 1 ? "" : "s")}"
        : $"{span.TotalMinutes:0} minute{(span.TotalMinutes == 1 ? "" : "s")}";
}
