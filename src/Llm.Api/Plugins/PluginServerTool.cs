using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Llm.Core.Data;

namespace Llm.Api.Plugins;

/// <summary>
/// A plugin whose tools are its own server code (tools: functions:), as a tool of the chat's: its functions are named
/// "{plugin}__{function}", each call goes to its worker in the plugin runtime as the person asking (in their chat), and a
/// function the manifest says writes asks first. Who may use it is set in Admin → Tools as for any tool; a plugin that
/// brings only pages is listed there too (that is where it is given to groups) but never offered in a chat.
/// </summary>
public sealed class PluginServerTool(McpServer server, PluginCalls calls, PluginRuntime runtime, IServiceScopeFactory scopes) : IServerTool
{
    private readonly PluginManifest _manifest = PluginManifest.Parse(server.Manifest!);

    public McpServer Server => server;
    public string Id => McpServerTool.Prefix + server.Id;
    public string Title => server.Name;
    public string Description => server.Description ?? $"{server.Name}'s own tools.";
    public string Icon => _manifest.Icon ?? "plug";
    public string Slug => McpServerTool.Slugify(server.Name);

    public async Task<string?> UnavailableAsync(CancellationToken ct)
    {
        if (_manifest.Functions.Length == 0)
        {
            return $"{server.Name} brings pages, not tools for the chat.";
        }
        if (runtime.Unavailable() is { } why)
        {
            return why;
        }
        await using var scope = scopes.CreateAsyncScope();
        return await PluginCalls.OffAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), server.Id, ct) is { } off ? $"{server.Name} is off. {off}" : null;
    }

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var prefix = Slug + "__";
        await using (var scope = scopes.CreateAsyncScope())
        {
            // A dev install is its developer's alone: nobody else is given its functions, wherever they ask from.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.PluginStates,
                s => s.ServerId == server.Id && s.DevOwnerId != null && s.DevOwnerId != context.User.Id, ct))
            {
                return new LocalRun([], null, (_, _, _) => Task.FromResult(new ToolResult("That is someone's dev install.", true)));
            }
        }
        var functions = new JsonArray([.. _manifest.Functions.Select(f => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = prefix + f.Name,
                ["description"] = f.Description,
                ["parameters"] = f.Parameters?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
        })]);
        IToolRun run = new LocalRun(functions, null, async (function, args, token) =>
        {
            var name = function.StartsWith(prefix, StringComparison.Ordinal) ? function[prefix.Length..] : function;
            var who = new PluginCallScope(server.Id, server.Plugin!, context.User.Id, context.Conversation.Id, context.Conversation.WorkProjectId, DateTimeOffset.MaxValue, false);
            var r = await calls.CallAsync(server, _manifest, "tool.call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, who, token);
            if (!r.Ok)
            {
                return new ToolResult($"{server.Name} failed: {r.Message}", true);
            }
            return new ToolResult(r.Result?["content"]?.GetValue<string>() ?? "", r.Result?["isError"]?.GetValue<bool>() == true);
        })
        {
            AsksFirst = function => function.StartsWith(prefix, StringComparison.Ordinal) && _manifest.Functions.Any(f => f.Writes && prefix + f.Name == function),
        };
        return run;
    }
}
