// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>What the model answered, or why it was not asked (the problem to return).</summary>
/// <param name="Unreached">The model could not be reached, or did not answer in time: worth asking again later (not a refusal).</param>
public sealed record Asked(string? Text, string? Model, IResult? Problem, bool Unreached = false);

/// <summary>
/// One question to the model as a person, outside a chat (an app's arena.ask, a question to the company's data): their
/// default model (or the model for small steps), their credit, their safeguards (the question's rules before, the
/// answer's after). No thinking: the answer is wanted at once.
/// </summary>
public sealed class OneQuestion(Safeguards.Safeguards safeguards, GatewayChat gateway, ModelPolicy policy, ChatModels models, IOptionsMonitor<ChatOptions> chat)
{
    /// <summary>
    /// The model to ask as the person: "small" for the model for small steps, a model's name when they may use it, else their
    /// default; or why there is none for them (none, or a rule refuses it).
    /// </summary>
    public async Task<(string? Model, IResult? Problem)> ModelAsync(AppUser me, string? choice, CancellationToken ct)
    {
        var mine = await policy.ForAsync(me, await models.ListAsync(ct), ct);
        var model = choice == "small" && chat.CurrentValue.SmallModel is { Length: > 0 } small && mine.Models.Any(m => m.Name == small) ? small
            : choice is { Length: > 0 } named && mine.Models.Any(m => m.Name == named) ? named
            : mine.Default?.Name;
        if (model is null)
        {
            return (null, AuthEndpoints.Problem(503, "model", "No model is there for you to ask."));
        }
        return await policy.RefusalAsync(me, model, ct) is { } refused ? (model, AuthEndpoints.Problem(403, "model", refused)) : (model, null);
    }

    /// <param name="question">The person's own words: the safeguards read these.</param>
    /// <param name="choice">"small" for the model for small steps, or a model's name; when the person may use it, else their default.</param>
    /// <param name="after">Turns after the question (an earlier answer and what was wrong with it), to ask again.</param>
    public async Task<Asked> AskAsync(AppUser me, string question, string? system, string? choice, int maxTokens, string tag, CancellationToken ct,
        IReadOnlyList<JsonObject>? after = null)
    {
        var (model, problem) = await ModelAsync(me, choice, ct);
        if (problem is not null)
        {
            return new(null, model, problem);
        }
        var verdict = await safeguards.CheckMessageAsync(me, question, 0, false, model, ct);
        if (!verdict.Allowed)
        {
            return new(null, model, Safeguards.Safeguards.Problem(verdict));
        }
        var messages = new JsonArray();
        if (system is { Length: > 0 })
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = verdict.Text ?? question });
        foreach (var turn in after ?? [])
        {
            messages.Add(turn.DeepClone());
        }
        var request = new JsonObject
        {
            ["model"] = verdict.Model ?? model, ["messages"] = messages, ["stream"] = true, ["max_tokens"] = Math.Clamp(maxTokens, 16, 8000), ["user"] = me.Email,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["metadata"] = new JsonObject { ["tags"] = new JsonArray(tag) },
        };
        var said = new StringBuilder();
        try
        {
            await foreach (var e in gateway.StreamAsync(request, me.Email!, ct))
            {
                if (e is ContentDelta c)
                {
                    said.Append(c.Text);
                }
            }
        }
        catch (ChatGatewayException ex)
        {
            return new(null, model, AuthEndpoints.Problem(502, "model", ex.Message), Unreached: true);
        }
        var answer = await safeguards.RunAsync(new Safeguards.RuleRun("answer", said.ToString()) { User = me, Model = model }, ct);
        return answer.Blocked
            ? new(null, model, AuthEndpoints.Problem(answer.Status, "safeguard", answer.Reason ?? "The answer was withheld by a rule."))
            : new(answer.Text, model, null);
    }
}
