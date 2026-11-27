// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Identity;

namespace Llm.Api.Plugins;

/// <summary>
/// A file a person uploads (to a chat, a meeting's recording, a deck read from its file), as plugins see it: the
/// file.uploaded.before_store hooks before it is kept (observed; its name changed by one granted files.write; refused by
/// one granted hooks.block), and file.uploaded once it is. A file a tool, a model or a plugin makes is not an upload: a
/// plugin's own files.put runs no hook (a hook that put a file would run itself again).
/// </summary>
public static class PluginUploads
{
    /// <summary>The name it is kept under, or why it is refused (said to the person).</summary>
    public static async Task<(string Name, string? Refused)> BeforeStoreAsync(IServiceProvider services, AppUser me, string name, string? contentType, CancellationToken ct)
    {
        var hooks = services.GetRequiredService<PluginHooks>();
        var outcome = await hooks.RunAsync("file.uploaded.before_store", me, null,
            new JsonObject { ["name"] = name, ["contentType"] = contentType ?? "application/octet-stream" }, ct);
        if (outcome.Blocked is { } refused)
        {
            return (name, refused);
        }
        // A name, not a path: a hook's name is kept only as a file's own name, of the length a name may have.
        var given = Path.GetFileName(outcome.Payload["name"]?.GetValue<string>() ?? "").Trim();
        return (given.Length is > 0 and <= 255 ? given : name, null);
    }

    /// <summary>Plugins that listen hear the file was kept (as the person, those the person may use).</summary>
    public static Task UploadedAsync(IServiceProvider services, ChatAttachment file, Guid person) =>
        services.GetRequiredService<PluginEvents>().EmitAsync("file.uploaded",
            new JsonObject { ["fileId"] = file.Id.ToString(), ["name"] = file.FileName, ["contentType"] = file.ContentType, ["size"] = file.Size }, person, CancellationToken.None);
}
