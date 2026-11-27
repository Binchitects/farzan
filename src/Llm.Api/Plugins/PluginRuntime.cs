// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Plugins;

/// <summary>Configuration section "PluginRuntime": where the plugin runtime is.</summary>
public sealed class PluginRuntimeOptions
{
    /// <summary>The plugin-runtime volume, as the app mounts it: the runtime's socket is run/host.sock in it.</summary>
    public string Root { get; set; } = "/plugin-runtime";
    /// <summary>Instead of the socket: host:port with <see cref="Key"/> (Kubernetes).</summary>
    public string? Tcp { get; set; }
    public string? Key { get; set; }
    /// <summary>How long a call to a plugin may take at most, whatever its manifest says.</summary>
    public int MostTimeoutMs { get; set; } = 60000;
    /// <summary>The most memory a plugin may ask for unless its admin raises it.</summary>
    public int MostMemoryMb { get; set; } = 512;
}

/// <summary>What a call to a plugin's worker came to: its result, or why not (kind: timeout, crash, protocol, memory, cpu, busy, error, off).</summary>
public sealed record PluginCallResult(JsonNode? Result, string? Kind, string? Message)
{
    public bool Ok => Kind is null;
    /// <summary>A failure that is the plugin's fault (counted by its breaker), not the person's or Arena's.</summary>
    public bool PluginsFault => Kind is "timeout" or "crash" or "protocol" or "memory" or "cpu" or "error";
}

/// <summary>Who a call is for, while it lasts: host API calls carrying its token act as this, and nothing else.</summary>
public sealed record PluginCallScope(Guid ServerId, string Plugin, Guid? UserId, Guid? ChatId, Guid? ProjectId, DateTimeOffset Until, bool Dev);

/// <summary>
/// The app's side of the plugin runtime (deploy/services/plugins): one connection to its socket, kept open and opened again
/// when it breaks; each plugin started on it with its package's files (verified at install, sent over the socket: the app
/// never writes the runtime's folders) and its settings; calls with their deadlines; and the workers' host API calls answered by <see cref="PluginHost"/>, as the
/// person whose call they were made in (its token), or as the plugin when there is none.
/// </summary>
public sealed partial class PluginRuntime(IOptionsMonitor<PluginRuntimeOptions> options, IServiceScopeFactory scopes, ILogger<PluginRuntime> log, TimeProvider clock) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, PluginCallScope> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Plugin, string Line)> _logs = new();
    private Stream? _stream;
    private CancellationTokenSource? _reading;
    private long _next;

    /// <summary>The last lines the plugins wrote (their logs), newest last; at most 2,000.</summary>
    public IEnumerable<(string Plugin, string Line)> Logs => _logs;

    /// <summary>When a worker ended, and why (for its health).</summary>
    public event Action<string, string>? Exited;

    public string SocketPath => Path.Combine(options.CurrentValue.Root, "run", "host.sock");

    /// <summary>Why the runtime cannot be used now, or null.</summary>
    public string? Unavailable() =>
        options.CurrentValue.Tcp is null && !File.Exists(SocketPath) ? "The plugin runtime is not running (the plugins feature: ./arenactl repair --with plugins)." : null;

    /// <summary>A token for one call: host API calls made while it runs act for its person, in its chat or project.</summary>
    public string Mint(PluginCallScope scope)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _tokens[token] = scope;
        foreach (var (t, s) in _tokens)
        {
            if (s.Until < clock.GetUtcNow())
            {
                _tokens.TryRemove(t, out _);
            }
        }
        return token;
    }

    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    /// <summary>The call a token was minted for, while it lasts.</summary>
    public PluginCallScope? Scope(string? token) =>
        token is not null && _tokens.TryGetValue(token, out var s) && s.Until >= clock.GetUtcNow() ? s : null;

    private async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        if (_stream is { } s)
        {
            return s;
        }
        await _connecting.WaitAsync(ct);
        try
        {
            if (_stream is { } again)
            {
                return again;
            }
            var o = options.CurrentValue;
            Socket socket;
            if (o.Tcp is { Length: > 0 } tcp)
            {
                var (host, port) = (tcp[..tcp.LastIndexOf(':')], int.Parse(tcp[(tcp.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, ct);
            }
            else
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
            }
            var stream = new NetworkStream(socket, ownsSocket: true);
            if (o.Tcp is { Length: > 0 })
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["type"] = "hello", ["key"] = o.Key }.ToJsonString() + "\n"), ct);
            }
            _started.Clear();
            _reading = new CancellationTokenSource();
            _stream = stream;
            _ = Task.Run(() => ReadAsync(stream, _reading.Token), CancellationToken.None);
            return stream;
        }
        finally
        {
            _connecting.Release();
        }
    }

    private void Broken(Stream stream, string why)
    {
        if (!ReferenceEquals(_stream, stream))
        {
            return;
        }
        _stream = null;
        _started.Clear();
        _reading?.Cancel();
        stream.Dispose();
        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                tcs.TrySetResult(new JsonObject { ["error"] = new JsonObject { ["kind"] = "off", ["message"] = $"The plugin runtime's connection broke: {why}" } });
            }
        }
        ConnectionBroke(log, why);
    }

    private async Task ReadAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 8 * 1024 * 1024 || JsonNode.Parse(line) is not JsonObject m)
                {
                    continue;
                }
                switch (m["type"]?.GetValue<string>())
                {
                    case "result" or "status":
                        if (m["id"]?.GetValue<long>() is { } id && _pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(m);
                        }
                        break;
                    case "host":
                        _ = Task.Run(() => HostAsync(m, ct), CancellationToken.None);
                        break;
                    case "exit":
                        var plugin = m["plugin"]?.GetValue<string>() ?? "";
                        Exited?.Invoke(plugin, m["reason"]?.GetValue<string>() ?? "ended");
                        break;
                    case "log":
                        _logs.Enqueue((m["plugin"]?.GetValue<string>() ?? "", m["line"]?.GetValue<string>() ?? ""));
                        while (_logs.Count > 2000)
                        {
                            _logs.TryDequeue(out _);
                        }
                        break;
                }
            }
            Broken(stream, "the runtime closed it");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Broken(stream, ex.Message);
        }
    }

    private async Task HostAsync(JsonObject m, CancellationToken ct)
    {
        var hid = m["hid"]!.GetValue<long>();
        JsonObject answer;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var host = scope.ServiceProvider.GetRequiredService<PluginHost>();
            var result = await host.HandleAsync(m["plugin"]?.GetValue<string>() ?? "", m["method"]?.GetValue<string>() ?? "",
                m["params"] as JsonObject ?? [], m["token"]?.GetValue<string>(), ct);
            answer = new JsonObject { ["type"] = "host.result", ["hid"] = hid, ["result"] = result };
        }
        catch (PluginHost.HostApiException ex)
        {
            answer = new JsonObject { ["type"] = "host.result", ["hid"] = hid, ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message } };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostCallFailed(log, ex);
            answer = new JsonObject { ["type"] = "host.result", ["hid"] = hid, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Arena could not answer that." } };
        }
        try
        {
            await SendAsync(answer, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection broke: the worker's call fails at its own deadline.
        }
    }

    private async Task SendAsync(JsonObject m, CancellationToken ct)
    {
        var stream = await ConnectAsync(ct);
        var bytes = Encoding.UTF8.GetBytes(m.ToJsonString() + "\n");
        await _writing.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Broken(stream, ex.Message);
            throw;
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task<JsonObject> RequestAsync(JsonObject m, TimeSpan wait, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _next);
        m["id"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(m, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JsonObject { ["error"] = new JsonObject { ["kind"] = "timeout", ["message"] = "The plugin runtime did not answer in time." } };
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return new JsonObject { ["error"] = new JsonObject { ["kind"] = "off", ["message"] = $"The plugin runtime cannot be reached: {ex.Message}" } };
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static PluginCallResult Result(JsonObject m) => m["error"] is JsonObject e
        ? new PluginCallResult(null, e["kind"]?.GetValue<string>() ?? "error", e["message"]?.GetValue<string>() ?? "failed")
        : new PluginCallResult(m["result"]?.DeepClone(), null, null);

    /// <summary>The package of the version installed, its every file (checked again as it is read), for the runtime to write and run.</summary>
    private static async Task<JsonObject> FilesAsync(AppDbContext db, McpServer server, CancellationToken ct)
    {
        var release = await db.PluginReleases.AsNoTracking().Where(r => r.ServerId == server.Id && r.Version == server.PluginVersion)
            .OrderByDescending(r => r.InstalledAt).Select(r => r.Id).FirstOrDefaultAsync(ct);
        var blob = await db.PluginPackageBlobs.AsNoTracking().SingleOrDefaultAsync(b => b.ReleaseId == release, ct)
            ?? throw new InvalidOperationException($"{server.Plugin} {server.PluginVersion} has no package kept: install it again.");
        var (files, _) = PluginFap.Read(blob.Bytes);
        // What its worker runs: not its pages and their pictures (the app serves those), nor its manifest.
        var manifest = PluginManifest.Parse(server.Manifest!);
        var pages = manifest.Ui.Pages.Select(p => p.Entry).Concat(manifest.Ui.Panels.Select(p => p.Entry)).Concat(manifest.Ui.IdePanels.Select(p => p.Entry))
            .Concat(manifest.Ui.ComposerActions.Concat(manifest.Ui.MessageActions).Select(a => a.Entry).OfType<string>())
            .Concat(manifest.Ui.Settings is { } st ? [st] : []).Select(e => e.Contains('/') ? e[..(e.LastIndexOf('/') + 1)] : "").Where(d => d.Length > 0).ToHashSet();
        var serverDir = manifest.Server!.Entry.Contains('/') ? manifest.Server.Entry[..(manifest.Server.Entry.LastIndexOf('/') + 1)] : "";
        var o = new JsonObject();
        foreach (var (name, data) in files.Where(f => f.Key != PluginManifest.FileName && (serverDir.Length > 0 && f.Key.StartsWith(serverDir, StringComparison.Ordinal)
            || !pages.Any(d => f.Key.StartsWith(d, StringComparison.Ordinal)))))
        {
            o[name] = Convert.ToBase64String(data);
        }
        return o;
    }

    /// <summary>Starts (or starts again) the plugin's worker with its package and settings: its initialize answer, or why not.</summary>
    public async Task<PluginCallResult> StartAsync(McpServer server, PluginManifest manifest, CancellationToken ct)
    {
        if (manifest.Server is not { } code)
        {
            return new PluginCallResult(null, "error", $"{manifest.Title} has no server code.");
        }
        if (Unavailable() is { } why)
        {
            return new PluginCallResult(null, "off", why);
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dataKey = scope.ServiceProvider.GetRequiredService<IOptions<Identity.AuthOptions>>().Value.DataKey;
        JsonObject files;
        try
        {
            files = await FilesAsync(db, server, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PluginFap.FapException)
        {
            return new PluginCallResult(null, "error", ex.Message);
        }
        var o = options.CurrentValue;
        var settings = new JsonObject();
        var values = PluginInstaller.Values(server, dataKey);
        foreach (var s in manifest.Settings)
        {
            var v = values.GetValueOrDefault(s.Key) ?? s.Default;
            if (v is null)
            {
                continue;
            }
            settings[s.Key] = s.Type switch
            {
                "number" when double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
                "boolean" => v is "true" or "on" or "1",
                _ => v,
            };
        }
        var granted = server.PluginPermissions.Where(p => p.StartsWith("scope:", StringComparison.Ordinal)).Select(p => p["scope:".Length..]).ToList();
        var hosts = server.PluginPermissions.Where(p => p.StartsWith("host:", StringComparison.Ordinal)).Select(p => p["host:".Length..]).ToList();
        var r = Result(await RequestAsync(new JsonObject
        {
            ["type"] = "start", ["plugin"] = manifest.Name, ["version"] = manifest.Version, ["files"] = files, ["entry"] = code.Entry,
            ["limits"] = new JsonObject
            {
                ["memoryMb"] = Math.Min(code.MemoryMb, o.MostMemoryMb), ["timeoutMs"] = Math.Min(code.TimeoutMs, o.MostTimeoutMs), ["concurrency"] = code.Concurrency,
            },
            ["init"] = new JsonObject
            {
                ["hostApi"] = PluginManifest.HostApiVersion, ["arena"] = new JsonObject { ["version"] = AppInfo.Current.Version },
                ["plugin"] = new JsonObject { ["name"] = manifest.Name, ["version"] = manifest.Version }, ["settings"] = settings,
                ["scopes"] = new JsonArray([.. granted.Select(g => (JsonNode)g)]), ["network"] = new JsonArray([.. hosts.Select(h => (JsonNode)h)]),
            },
        }, TimeSpan.FromMilliseconds(Math.Min(code.TimeoutMs, o.MostTimeoutMs) + 15000), ct));
        if (r.Ok)
        {
            var declared = manifest.Functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var served = (r.Result?["tools"] as JsonArray ?? []).Select(t => t?["name"]?.GetValue<string>()).OfType<string>().ToList();
            if (served.FirstOrDefault(n => !declared.Contains(n)) is { } extra)
            {
                await StopAsync(manifest.Name, ct);
                return new PluginCallResult(null, "protocol", $"Its server has a tool its manifest does not list ({extra}): it is not run.");
            }
            _started[manifest.Name] = manifest.Version;
        }
        return r;
    }

    public async Task StopAsync(string plugin, CancellationToken ct)
    {
        _started.TryRemove(plugin, out _);
        if (Unavailable() is null)
        {
            await RequestAsync(new JsonObject { ["type"] = "stop", ["plugin"] = plugin }, TimeSpan.FromSeconds(10), ct);
        }
    }

    /// <summary>One call to the plugin's worker (started first when it is not on this connection), within its deadline.</summary>
    public async Task<PluginCallResult> CallAsync(McpServer server, PluginManifest manifest, string method, JsonObject args, PluginCallScope who, CancellationToken ct, int? timeoutMs = null)
    {
        if (manifest.Server is not { } code)
        {
            return new PluginCallResult(null, "error", $"{manifest.Title} has no server code.");
        }
        if (!_started.TryGetValue(manifest.Name, out var version) || version != manifest.Version)
        {
            var started = await StartAsync(server, manifest, ct);
            if (!started.Ok)
            {
                return started;
            }
        }
        var timeout = Math.Min(timeoutMs ?? code.TimeoutMs, Math.Min(code.TimeoutMs, options.CurrentValue.MostTimeoutMs));
        var token = Mint(who with { Until = clock.GetUtcNow().AddMilliseconds(timeout + 5000) });
        try
        {
            args["context"] = new JsonObject
            {
                ["token"] = token,
                ["person"] = who.UserId is { } uid ? await PersonAsync(uid, ct) : null,
                ["chatId"] = who.ChatId?.ToString(), ["projectId"] = who.ProjectId?.ToString(),
                ["deadline"] = clock.GetUtcNow().AddMilliseconds(timeout).ToUnixTimeMilliseconds(), ["dev"] = who.Dev,
            };
            var m = new JsonObject { ["type"] = "call", ["plugin"] = manifest.Name, ["method"] = method, ["params"] = args, ["timeoutMs"] = timeout, ["token"] = token };
            var r = Result(await RequestAsync(m, TimeSpan.FromMilliseconds(timeout + 5000), ct));
            if (r.Kind == "off" && r.Message?.Contains("not started here", StringComparison.Ordinal) == true)
            {
                // The runtime started again since: start it there, and ask once more.
                _started.TryRemove(manifest.Name, out _);
                var started = await StartAsync(server, manifest, ct);
                if (!started.Ok)
                {
                    return started;
                }
                m.Remove("id");
                r = Result(await RequestAsync(m, TimeSpan.FromMilliseconds(timeout + 5000), ct));
            }
            return r;
        }
        finally
        {
            Revoke(token);
        }
    }

    private async Task<JsonObject?> PersonAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.UserName, x.DisplayName, x.Email }).SingleOrDefaultAsync(ct);
        if (u is null)
        {
            return null;
        }
        var groups = await db.GroupMembers.AsNoTracking().Where(g => g.UserId == id).Join(db.Groups.AsNoTracking(), m => m.GroupId, g => g.Id, (m, g) => g.Name).ToListAsync(ct);
        return new JsonObject
        {
            ["id"] = u.Id.ToString(), ["name"] = u.DisplayName.Length > 0 ? u.DisplayName : u.UserName, ["email"] = u.Email,
            ["groups"] = new JsonArray([.. groups.Select(g => (JsonNode)g)]),
        };
    }

    /// <summary>What the runtime runs: each plugin's worker, its memory and restarts.</summary>
    public async Task<JsonArray> StatusAsync(CancellationToken ct)
    {
        if (Unavailable() is not null)
        {
            return [];
        }
        var m = await RequestAsync(new JsonObject { ["type"] = "status" }, TimeSpan.FromSeconds(5), ct);
        return m["plugins"] as JsonArray ?? [];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The plugin runtime's connection broke: {Why}")]
    private static partial void ConnectionBroke(ILogger logger, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A plugin's host API call failed")]
    private static partial void HostCallFailed(ILogger logger, Exception ex);

    public async ValueTask DisposeAsync()
    {
        _reading?.Cancel();
        if (_stream is { } s)
        {
            await s.DisposeAsync();
        }
        _connecting.Dispose();
        _writing.Dispose();
    }
}
