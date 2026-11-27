using System.Security.Cryptography;
using System.Text.Json;
using Llm.Api.Plugins;

namespace Llm.Tests;

/// <summary>
/// Arena held to the plugin framework's conformance suite (tests/fixtures/plugins, from the commit plugins/framework.lock
/// names): each manifest refused with the code the framework gives it, or read; each package read, or refused with the
/// framework's code, and signed by the publisher's key when the framework says so. The fixtures' hashes must be the lock's.
/// </summary>
public sealed class PluginConformanceTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppFixture.PluginsPath, "..", "tests", "fixtures", "plugins"));

    public static TheoryData<string> Manifests() => [.. Directory.GetFiles(Path.Combine(Root, "manifests"), "*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).OfType<string>()];

    public static TheoryData<string> Packages() => [.. Directory.GetFiles(Path.Combine(Root, "packages"), "*.fap").Select(Path.GetFileName).Order(StringComparer.Ordinal).OfType<string>()];

    [Theory]
    [MemberData(nameof(Manifests))]
    public void Each_manifest_is_read_or_refused_as_the_framework_says(string file)
    {
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifests", "expected.json"))).RootElement.GetProperty(file)
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var text = File.ReadAllText(Path.Combine(Root, "manifests", file));
        if (expected.Count == 0)
        {
            var m = PluginManifest.Parse(text);
            Assert.False(string.IsNullOrEmpty(m.Name));
            return;
        }
        var ex = Assert.Throws<PluginManifest.ManifestException>(() => PluginManifest.Parse(text));
        Assert.Contains(ex.Code, expected);
    }

    [Theory]
    [Trait("Control", "OPS-03")]
    [MemberData(nameof(Packages))]
    public void Each_package_is_read_or_refused_as_the_framework_says(string file)
    {
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "packages", "expected.json"))).RootElement.GetProperty(file);
        var bytes = File.ReadAllBytes(Path.Combine(Root, "packages", file));
        var pem = File.ReadAllText(Path.Combine(Root, "keys", "publisher.pub.pem"));
        if (expected.GetProperty("ok").GetBoolean())
        {
            var p = PluginCatalog.FromFap(bytes, [pem]);
            Assert.Equal(expected.GetProperty("signed").GetBoolean(), p.Signature.IsSigned);
            Assert.Equal(expected.GetProperty("trusted").GetBoolean(), p.Signature.Trusted);
            return;
        }
        var ex = Assert.Throws<PluginCatalog.PluginException>(() => PluginCatalog.FromFap(bytes, [pem]));
        Assert.Contains(ex.Code, expected.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void The_fixtures_are_the_framework_commit_the_lock_names()
    {
        var lines = File.ReadAllLines(Path.Combine(AppFixture.PluginsPath, "framework.lock"));
        Assert.Contains(lines, l => l.StartsWith("commit: ", StringComparison.Ordinal) && l.Length == "commit: ".Length + 40);
        var listed = lines.SkipWhile(l => l != "files:").Skip(1).Select(l => l.Trim().Split(": ")).ToDictionary(p => p[0], p => p[1]);
        var present = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).ToHashSet();
        Assert.Equal(listed.Keys.Order(StringComparer.Ordinal), present.Order(StringComparer.Ordinal));
        foreach (var (name, sha) in listed)
        {
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, name)))));
        }
    }

    [Theory]
    [InlineData("8.3.0", ">=8.3.0 <9.0.0", true)]
    [InlineData("9.0.0", ">=8.3.0 <9.0.0", false)]
    [InlineData("8.4.2", "^8.3", true)]
    [InlineData("8.2.9", "^8.3", false)]
    [InlineData("1.2.9", "~1.2.0", true)]
    [InlineData("1.3.0", "~1.2.0", false)]
    [InlineData("8.7.1", "8.x", true)]
    [InlineData("1.5.0", "1.0.0 - 2.0.0", true)]
    [InlineData("3.1.0", "^1 || ^3", true)]
    [InlineData("8.3.0-rc.1", ">=8.3.0", false)]
    [InlineData("8.3.0-rc.2", ">=8.3.0-rc.1", true)]
    public void Ranges_read_as_the_framework_reads_them(string version, string range, bool inside) =>
        Assert.Equal(inside, PluginRules.Satisfies(version, range));
}
