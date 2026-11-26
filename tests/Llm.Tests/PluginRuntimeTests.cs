using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The plugin runtime's image (deploy/services/plugins), started once for these tests, reached over TCP with a key.</summary>
public sealed class PluginRuntimeFixture : IAsyncLifetime
{
    public string Key { get; } = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    public string Address => $"127.0.0.1:{_container!.GetMappedPublicPort(7700)}";
    private IFutureDockerImage? _image;
    private IContainer? _container;

    public async Task InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.GetFullPath(Path.Combine(AppFixture.PluginsPath, "..", "deploy", "services", "plugins")))
            .WithName($"arena-plugins:test-{Guid.NewGuid():N}"[..40]).WithCleanUp(true).WithDeleteIfExists(true).Build();
        await _image.CreateAsync();
        _container = new ContainerBuilder(_image)
            .WithEnvironment("PLUGINS_LISTEN", "tcp:0.0.0.0:7700").WithEnvironment("PLUGINS_KEY", Key).WithEnvironment("PLUGINS_WATCH_MS", "100")
            .WithPortBinding(7700, true).WithTmpfsMount("/work").WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("listening on"))
            .Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        if (_image is not null)
        {
            await _image.DisposeAsync();
        }
    }
}

/// <summary>
/// Plugins v3 through the app and the real runtime: a signed package installed only once its publisher is trusted, its
/// tool and its route as a person, its frames' host API calls checked by its grants, a grant taken back refused at once,
/// its UI files served sandboxed, a dev install by API key; and the chaos plugin turned off by its breaker after five
/// crashes in a row, its admins told and the audit log saying so, and on again by an admin.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class PluginRuntimeTests(AppFixture app, PluginRuntimeFixture runtime) : IClassFixture<PluginRuntimeFixture>
{
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppFixture.PluginsPath, "..", "tests", "fixtures", "plugins"));

    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? more = null) =>
        app.Create(app.ConnectionStringFor("pluginrt_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>(more ?? [])
        {
            ["Auth:DataKey"] = "a-data-key-for-plugin-runtime-tests", ["PluginRuntime:Tcp"] = runtime.Address, ["PluginRuntime:Key"] = runtime.Key,
            ["Plugins:DevInstalls"] = "true",
        });

    private static string Fap(string name) => Convert.ToBase64String(File.ReadAllBytes(Path.Combine(Fixtures, "packages", name + ".fap")));

    private static async Task<HttpResponseMessage> BridgeAsync(TestBrowser b, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Arena-Bridge", "1");
        return await b.Http.SendAsync(req);
    }

    private static async Task TrustAsync(TestBrowser admin) =>
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/plugins/publishers",
            new { name = "Framework examples", publicKeyPem = await File.ReadAllTextAsync(Path.Combine(Fixtures, "keys", "publisher.pub.pem")) })).StatusCode);

    private static async Task<Guid> InstallAsync(TestBrowser admin, string name)
    {
        var res = await admin.PostAsync("/api/admin/plugins/install", new { zip = Fap(name), accept = true });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await admin.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_signed_plugin_installs_once_its_publisher_is_trusted_and_its_calls_are_checked_by_its_grants()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        // Not yet trusted: code from a key nobody here trusts does not install.
        var refused = await admin.PostAsync("/api/admin/plugins/install", new { zip = Fap("ticket-desk"), accept = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not trusted here", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await TrustAsync(admin);
        var id = await InstallAsync(admin, "ticket-desk");
        var state = await admin.JsonAsync(await admin.GetAsync($"/api/admin/plugins/{id}/state"));
        Assert.Equal("on", state.GetProperty("state").GetString());
        Assert.Equal("Framework examples", state.GetProperty("publisher").GetString());

        // Its tool, from a frame, as the person: one that writes needs their yes first.
        var unconfirmed = await BridgeAsync(admin, "/api/plugins/ticket-desk/tools/create_ticket", new { arguments = new { title = "The VPN drops" } });
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.StatusCode);
        var made = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/tools/create_ticket", new { arguments = new { title = "The VPN drops" }, confirmed = true }));
        Assert.StartsWith("Opened T-1 [open] The VPN drops", made.GetProperty("content").GetString(), StringComparison.Ordinal);

        // Its routes: GET as the person; a change only through the bridge (another site cannot send its header).
        var list = await admin.JsonAsync(await admin.GetAsync("/api/p/ticket-desk/tickets"));
        Assert.Equal("T-1", list[0].GetProperty("id").GetString());
        var noBridge = await admin.PostAsync("/api/p/ticket-desk/tickets", new { title = "From another site" });
        Assert.Equal(HttpStatusCode.Forbidden, noBridge.StatusCode);
        var posted = await BridgeAsync(admin, "/api/p/ticket-desk/tickets", new { title = "Printer" });
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        Assert.Contains("sandbox", posted.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        // Its frames' host API calls: what it was granted, and nothing it was not.
        var caps = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/host", new { method = "capabilities" }));
        Assert.Contains("storage", caps.GetProperty("result").GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        var people = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/host", new { method = "people.search", @params = new { query = "a" } }));
        Assert.Equal(-32001, people.GetProperty("error").GetProperty("code").GetInt32());
        var fetch = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/host", new { method = "http.fetch", @params = new { url = "https://evil.example.net/" } }));
        Assert.Equal(-32001, fetch.GetProperty("error").GetProperty("code").GetInt32());

        // A grant taken back is refused from the next call on.
        var grants = await admin.JsonAsync(await admin.GetAsync($"/api/admin/plugins/{id}/grants"));
        var keep = grants.EnumerateArray().Select(g => g.GetProperty("key").GetString()!).Where(k => k != "scope:storage").ToList();
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/plugins/{id}/grants", UriKind.Relative), new { granted = keep })).StatusCode);
        var denied = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/tools/list_tickets", new { arguments = new { } }));
        Assert.True(denied.GetProperty("isError").GetBoolean());
        Assert.Contains("not granted storage", denied.GetProperty("content").GetString(), StringComparison.Ordinal);
        var beyond = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/plugins/{id}/grants", UriKind.Relative), new { granted = new[] { "scope:people.read" } });
        Assert.Equal(HttpStatusCode.BadRequest, beyond.StatusCode);

        // Its pages: listed for the person, their files served under the frames' policy, nothing else of the package.
        var mine = await admin.JsonAsync(await admin.GetAsync("/api/plugins/mine"));
        var desk = mine.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "ticket-desk");
        var basePath = desk.GetProperty("base").GetString()!;
        var page = await admin.GetAsync(basePath + "ui/board.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.StartsWith("sandbox allow-scripts", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("SAMEORIGIN", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(basePath + "server/index.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(basePath.Replace(basePath.Split('/')[^2], new string('0', 32), StringComparison.Ordinal) + "ui/board.html")).StatusCode);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AuditEvents.AsNoTracking().Where(a => a.Action.StartsWith("plugin.")).Select(a => a.Action).ToListAsync();
        Assert.Contains("plugin.publisher_trusted", actions);
        Assert.Contains("plugin.install", actions);
        Assert.Contains("plugin.ticket-desk.ticket.created", actions);
        Assert.Contains("plugin.denied", actions);
        Assert.Contains("plugin.grants", actions);
    }

    [Fact]
    public async Task A_plugin_that_crashes_five_times_in_a_row_is_turned_off_by_itself_and_on_again_by_an_admin()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await TrustAsync(admin);
        var chaos = await InstallAsync(admin, "chaos");
        var hello = await InstallAsync(admin, "hello");
        async Task<JsonElement> Tool(string plugin, string name, object? args = null)
        {
            var res = await BridgeAsync(admin, $"/api/plugins/{plugin}/tools/{name}", new { arguments = args ?? new { } });
            var json = await admin.JsonAsync(res);
            Assert.True(json.TryGetProperty("content", out _), $"{plugin}.{name}: {(int)res.StatusCode} {json}");
            return json;
        }

        Assert.Equal("fine", (await Tool("chaos", "ok")).GetProperty("content").GetString());
        for (var i = 0; i < 5; i++)
        {
            var crashed = await Tool("chaos", "exit");
            Assert.True(crashed.GetProperty("isError").GetBoolean());
            // The plugin beside it answers all along.
            Assert.Contains("Hello", (await Tool("hello", "greet", new { name = "Bob" })).GetProperty("content").GetString(), StringComparison.Ordinal);
        }
        var state = await admin.JsonAsync(await admin.GetAsync($"/api/admin/plugins/{chaos}/state"));
        Assert.Equal("tripped", state.GetProperty("state").GetString());
        Assert.Contains("5 failures in a row", state.GetProperty("reason").GetString(), StringComparison.Ordinal);
        // Off for everyone: its tools, routes and frames are not there until an admin turns it on.
        Assert.Equal(HttpStatusCode.NotFound, (await BridgeAsync(admin, "/api/plugins/chaos/tools/ok", new { arguments = new { } })).StatusCode);
        Assert.DoesNotContain((await admin.JsonAsync(await admin.GetAsync("/api/plugins/mine"))).EnumerateArray(), p => p.GetProperty("name").GetString() == "chaos");

        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == "plugin.auto_disabled" && a.Target == "chaos"));
            Assert.True(await db.Notifications.AnyAsync(n => n.Title.Contains("Chaos was turned off")));
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/plugins/{chaos}/state", UriKind.Relative), new { on = true })).StatusCode);
        Assert.Equal("fine", (await Tool("chaos", "ok")).GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/admin/plugins/{hello}/runtime")).StatusCode);
    }

    [Fact]
    public async Task A_chat_message_runs_the_plugins_hooks_and_events_and_a_failing_hook_never_stops_the_chat()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await TrustAsync(admin);
        await InstallAsync(admin, "ticket-desk");
        // The chaos plugin's before_model hook throws: it observes only and does not fail closed, so it is skipped.
        await InstallAsync(admin, "chaos");
        var chat = (await admin.JsonAsync(await admin.PostAsync("/api/chat/conversations", new { useCodeIntel = false }))).GetProperty("id").GetGuid();
        var sent = await (await admin.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Is T-1 fixed, and T-2?" })).Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"done\"", sent, StringComparison.Ordinal);

        var host = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/host", new { method = "storage.get", @params = new { key = "mentions" } }));
        Assert.Equal(2, host.GetProperty("result").GetInt32());

        // The event waits in the outbox until the background work delivers it, as the person.
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.PluginEvents.AnyAsync(e => e.Type == "chat.message.created"));
        var work = f.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Llm.Api.Plugins.PluginWork>().Single();
        Assert.True(await work.EventsAsync(CancellationToken.None) >= 1);
        Assert.False(await db.PluginEvents.AnyAsync(e => e.Type == "chat.message.created" && e.ServerId == db.McpServers.Single(s => s.Plugin == "ticket-desk").Id));
        var last = await admin.JsonAsync(await BridgeAsync(admin, "/api/plugins/ticket-desk/host", new { method = "storage.get", @params = new { key = "last-message-at" } }));
        Assert.Equal(JsonValueKind.String, last.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_developer_pushes_a_dev_install_with_their_api_key_that_only_they_see()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "d" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var dev = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        using var cli = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}") });
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        cli.DefaultRequestHeaders.Add("X-Requested-With", "arena-plugin");
        async Task<HttpResponseMessage> Push(string query)
        {
            using var content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(Fixtures, "packages", "hello.fap")));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.farzan-arena.plugin");
            return await cli.PostAsync(new Uri($"/api/plugins/packages{query}", UriKind.Relative), content);
        }

        var notInstaller = await Push("");
        Assert.True(notInstaller.StatusCode == HttpStatusCode.Forbidden, await notInstaller.Content.ReadAsStringAsync());
        var pushed = await Push("?dev=true");
        var body = await pushed.Content.ReadAsStringAsync();
        Assert.True(pushed.IsSuccessStatusCode, body);
        var devName = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
        Assert.StartsWith("hello--dev-", devName, StringComparison.Ordinal);
        Assert.Contains("Hello", (await dev.JsonAsync(await BridgeAsync(dev, $"/api/plugins/{devName}/tools/greet", new { arguments = new { name = "Me" } })))
            .GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await BridgeAsync(admin, $"/api/plugins/{devName}/tools/greet", new { arguments = new { name = "Me" } })).StatusCode);
        Assert.True((await Push("?dev=true")).IsSuccessStatusCode, "pushed again: a new dev version");
    }
}
