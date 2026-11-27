// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json.Nodes;
using Llm.Core.Identity;

namespace Llm.Api.Chat;

public sealed partial class ChatService
{
    /// <summary>The plugins' chat.tool.before_call hooks on a call: why it stops, or the arguments it runs with.</summary>
    private async Task<(string? Stopped, JsonObject Args)> ToolHookAsync(AppUser user, Guid chatId, string function, JsonObject args, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var hooks = scope.ServiceProvider.GetRequiredService<Plugins.PluginHooks>();
        var outcome = await hooks.RunAsync("chat.tool.before_call", user, chatId, new JsonObject { ["tool"] = function, ["arguments"] = args.DeepClone() }, ct);
        return (outcome.Blocked, outcome.Payload["arguments"] as JsonObject ?? args);
    }

    /// <summary>The plugins' chat.message.after_model hooks on a finished answer: why it is withheld, or the words the person reads.</summary>
    private async Task<(string? Stopped, string Content)> AnswerHookAsync(AppUser user, Guid chatId, string content, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var hooks = scope.ServiceProvider.GetRequiredService<Plugins.PluginHooks>();
        var outcome = await hooks.RunAsync("chat.message.after_model", user, chatId, new JsonObject { ["content"] = content, ["chatId"] = chatId.ToString() }, ct);
        return (outcome.Blocked, outcome.Payload["content"]?.GetValue<string>() ?? content);
    }
}
