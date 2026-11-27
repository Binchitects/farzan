// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
namespace Llm.Api.Quality;

/// <summary>Configuration section "Quality": feedback on answers and the arena.</summary>
public sealed class QualityOptions
{
    /// <summary>Everyone sees the arena's leaderboard; off: admins only (Admin → Quality).</summary>
    public bool PublicLeaderboard { get; set; } = true;
}
