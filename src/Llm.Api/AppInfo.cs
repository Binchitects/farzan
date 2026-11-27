// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Reflection;

using Branding;
namespace Llm.Api;

public sealed record AppInfo(string Name, string Version)
{
    public static AppInfo Current { get; } = new(
        $"{Brand.Product}",
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0");
}
