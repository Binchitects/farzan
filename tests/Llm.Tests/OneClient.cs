// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
namespace Llm.Tests;

/// <summary>An HTTP client factory whose every client goes to one handler (the handler outlives them).</summary>
public sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
