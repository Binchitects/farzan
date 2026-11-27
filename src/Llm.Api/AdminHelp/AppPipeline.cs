// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace Llm.Api.AdminHelp;

/// <summary>
/// The app's own request pipeline, to send a request through it as the person whose request this is: the same
/// sign-in (their session cookie), the same checks (CSRF, second factor, notice, permissions, groups' scope, rate
/// limits) and the same endpoint, in this process. The admin assistant reads and changes only this way, so it can never
/// do more than the person could themself. Each such request carries <see cref="Header"/>: the audit log says it came
/// through the assistant.
/// </summary>
public sealed class AppPipeline(IHttpContextFactory contexts)
{
    public const string Header = "X-Arena-Assistant";

    /// <summary>The most of an answer kept (a larger one is cut, and said so).</summary>
    public const int MostBytes = 512 * 1024;

    private RequestDelegate? app;

    /// <summary>Builds the whole pipeline (routing, the app's middleware, its endpoints) once, runs it, and keeps it.</summary>
    public sealed class Filter(AppPipeline pipeline) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => root =>
        {
            var inner = root.New();
            next(inner);
            var built = inner.Build();
            pipeline.app = built;
            root.Run(built);
        };
    }

    public sealed record Answer(int Status, string Body, string? ContentType, bool Cut);

    /// <summary>
    /// <paramref name="method"/> <paramref name="pathAndQuery"/> (with a JSON body, or none) as the person of <paramref name="from"/>:
    /// their cookies, address and host. At most 30 seconds, and <see cref="MostBytes"/> of its answer.
    /// </summary>
    public async Task<Answer> SendAsync(HttpContext from, string method, string pathAndQuery, string? json, CancellationToken ct)
    {
        var run = app ?? throw new InvalidOperationException("The app's pipeline is not built yet.");
        var at = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        IHeaderDictionary headers = new HeaderDictionary();
        foreach (var name in new[] { "Cookie", "Host", "User-Agent", "Accept-Language", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host" })
        {
            if (from.Request.Headers.TryGetValue(name, out var v))
            {
                headers[name] = v;
            }
        }
        headers["X-Requested-With"] = "fetch";
        headers["Accept"] = "application/json";
        headers[Header] = "1";
        var bytes = json is null ? [] : Encoding.UTF8.GetBytes(json);
        if (json is not null)
        {
            headers.ContentType = "application/json";
            headers.ContentLength = bytes.Length;
        }
        var request = new HttpRequestFeature
        {
            Method = method.ToUpperInvariant(), Scheme = from.Request.Scheme, Protocol = "HTTP/1.1", PathBase = "",
            Path = at < 0 ? pathAndQuery : pathAndQuery[..at], QueryString = at < 0 ? "" : pathAndQuery[at..],
            Headers = headers, Body = new MemoryStream(bytes),
        };
        var body = new Capped(MostBytes);
        var response = new HttpResponseFeature();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(request);
        // Minimal APIs read a body only where the request says it may have one, as a server's own request does.
        features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(json is not null));
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = timeout.Token });
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            ConnectionId = Guid.NewGuid().ToString("N"), RemoteIpAddress = from.Connection.RemoteIpAddress, RemotePort = from.Connection.RemotePort,
            LocalIpAddress = from.Connection.LocalIpAddress, LocalPort = from.Connection.LocalPort,
        });
        // A flow of its own: the request's context is set for it alone, and the person's own request keeps theirs.
        Task work;
        using (ExecutionContext.SuppressFlow())
        {
            work = Task.Run(async () =>
            {
                var ctx = contexts.Create(features);
                try
                {
                    await run(ctx);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    response.StatusCode = StatusCodes.Status504GatewayTimeout;
                }
                finally
                {
                    contexts.Dispose(ctx);
                }
            }, CancellationToken.None);
        }
        await work;
        ct.ThrowIfCancellationRequested();
        return new(response.StatusCode, Encoding.UTF8.GetString(body.ToArray()), response.Headers.ContentType.ToString() is { Length: > 0 } t ? t : null, body.WasCut);
    }

    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;

    /// <summary>A body that keeps at most its size, and the rest is let go (said: cut).</summary>
    private sealed class Capped(int most) : MemoryStream
    {
        public bool WasCut { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = Math.Max(0, most - (int)Length);
            base.Write(buffer, offset, Math.Min(room, count));
            WasCut |= count > room;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var room = Math.Max(0, most - (int)Length);
            base.Write(buffer[..Math.Min(room, buffer.Length)]);
            WasCut |= buffer.Length > room;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
