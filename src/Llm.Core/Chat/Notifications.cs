namespace Llm.Core.Chat;

/// <summary>
/// A scheduled automation a plugin offers (its manifest's tasks:): people add it to their own from Automations → From
/// plugins, a workflow on that schedule that asks its prompt, and change it there as any of theirs. Gone with the plugin;
/// the workflows made from it stay.
/// </summary>
public sealed class AutomationTemplate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ServerId { get; set; }
    public required string Plugin { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Prompt { get; set; }
    public required string Cron { get; set; }
    /// <summary>Its tools by id (the plugin's own as mcp:{id}); empty: the tools on in new chats.</summary>
    public List<string> Tools { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Something for a person to see: a workflow's news, an answer written while nobody watched. Shown under the bell.</summary>
public sealed class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    /// <summary>What it is about: answer (one written while nobody watched), task, usage (credit), alert (the system's, for admins), download.</summary>
    public string Kind { get; set; } = "task";
    public required string Title { get; set; }
    public string? Body { get; set; }
    /// <summary>Where it leads in the app, e.g. /chat/{id}.</summary>
    public string? Link { get; set; }
    /// <summary>What it is the news of, once per person (e.g. one alert's firing, a credit threshold): it is not said twice.</summary>
    public string? Key { get; set; }
    /// <summary>Cleared by the person: news said once (with a key) stays as a record, hidden, so it is not said again.</summary>
    public bool Cleared { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}
