// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
namespace Llm.Api.Settings;

/// <summary>
/// A setting whose value can be wrong for the rest of the deployment (a model that is not
/// kept loaded, say): the Settings page shows why under it, while it is so.
/// </summary>
public interface ISettingWarning
{
    /// <summary>The setting it is about (Chat:SmallModel).</summary>
    string Key { get; }

    /// <summary>What is wrong now, for the admin; null when nothing is.</summary>
    Task<string?> WarningAsync(CancellationToken ct = default);
}
