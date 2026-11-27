// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
namespace Llm.Core.Chat;

/// <summary>Whether a plugin runs: on, turned off by an admin, or turned off by itself after failing (its breaker).</summary>
public static class PluginStates
{
    public const string On = "on";
    public const string Off = "off";
    public const string Tripped = "tripped";
}

/// <summary>
/// A plugin's state beside its tool row (plugins v3): on or off and why, who signed the package installed, and for a dev
/// install, whose it is (only they see it). One row a plugin; a plugin without one (installed before 8.3) is on.
/// </summary>
public sealed class PluginState
{
    public Guid ServerId { get; set; }
    public string State { get; set; } = PluginStates.On;
    /// <summary>Why it is off: the admin's words, or what tripped it.</summary>
    public string? Reason { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ChangedById { get; set; }
    /// <summary>The fingerprint of the key that signed the package installed (a trusted publisher's), or null: not signed by one.</summary>
    public string? Signer { get; set; }
    /// <summary>Signed by a key nobody here trusts: its fingerprint.</summary>
    public string? UntrustedSigner { get; set; }
    /// <summary>A dev install (arena-plugin dev): the developer's own; nobody else sees it or its events.</summary>
    public Guid? DevOwnerId { get; set; }
}

/// <summary>A key whose packages are trusted here (Admin → Plugins → Trusted publishers): an ECDSA P-256 public key.</summary>
public sealed class PluginPublisher
{
    /// <summary>SHA-256 of the key (SPKI, DER), hex.</summary>
    public required string Fingerprint { get; set; }
    public required string Name { get; set; }
    public required string PublicKeyPem { get; set; }
    public Guid? AddedById { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may configure one plugin besides those whose role says so: a person, or a group's people.</summary>
public sealed class PluginManager
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ServerId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? GroupId { get; set; }
}

/// <summary>A value a plugin keeps (host API storage): plugin-wide (OwnerId empty) or one person's.</summary>
public sealed class PluginStoreEntry
{
    public Guid ServerId { get; set; }
    /// <summary>Whose: a person, or <see cref="Guid.Empty"/> for the plugin's own.</summary>
    public Guid OwnerId { get; set; }
    public required string Key { get; set; }
    public string ValueJson { get; set; } = "null";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A plugin's job and when it runs next: claimed by moving NextAt on (one replica runs it), as Automations' are.</summary>
public sealed class PluginJob
{
    public Guid ServerId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset NextAt { get; set; }
    public DateTimeOffset? LastAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An event for a plugin, delivered at least once: kept until delivered, tried again with a wait, dropped after so many tries.</summary>
public sealed class PluginEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ServerId { get; set; }
    public required string Type { get; set; }
    public string DataJson { get; set; } = "{}";
    /// <summary>The person it is about (the plugin hears it as them), or null.</summary>
    public Guid? PersonId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.UtcNow;
    public int Tries { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A v3 package as it was installed, all its files (UI pictures too), so a page or a worker has them, and a rollback.</summary>
public sealed class PluginPackageBlob
{
    public Guid ReleaseId { get; set; }
    public required byte[] Bytes { get; set; }
    public required string Sha256 { get; set; }
}
