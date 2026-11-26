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
}
