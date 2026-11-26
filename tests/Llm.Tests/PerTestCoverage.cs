using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Xml;
using Xunit.Sdk;

[assembly: Llm.Tests.PerTestCoverage]

namespace Llm.Tests;

/// <summary>
/// Coverage per test (tools/coverage-per-test.sh): while the run is under dotnet-coverage (ARENA_COVER_SESSION its
/// session), after each test its coverage is taken and the counts reset (what ran between tests goes to the next), and the lines it ran are kept, compressed, as
/// ARENA_COVER_DIR/{class}.{method}.{n}.lines.gz ("file:line" each). tools/coverage-redundant.py reads them. Outside such
/// a run it does nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PerTestCoverageAttribute : BeforeAfterTestAttribute
{
    private static readonly string? Session = Environment.GetEnvironmentVariable("ARENA_COVER_SESSION") is { Length: > 0 } s ? s : null;
    private static readonly string? Dir = Environment.GetEnvironmentVariable("ARENA_COVER_DIR") is { Length: > 0 } d ? d : null;
    private static int _n;

    public override void After(MethodInfo methodUnderTest)
    {
        if (Session is null || Dir is null)
        {
            return;
        }
        var name = $"{methodUnderTest.DeclaringType?.Name}.{methodUnderTest.Name}.{Interlocked.Increment(ref _n)}";
        var xml = Path.Combine(Dir, name + ".xml");
        Snapshot(xml);
        if (!File.Exists(xml))
        {
            return;
        }
        // The lines it ran, by file: a few kilobytes, where the report is megabytes.
        using (var gz = new GZipStream(File.Create(Path.Combine(Dir, name + ".lines.gz")), CompressionLevel.Fastest))
        using (var w = new StreamWriter(gz))
        using (var r = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
        {
            string? file = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                if (r.Name == "class")
                {
                    file = r.GetAttribute("filename");
                }
                else if (r.Name == "line" && file is not null && r.GetAttribute("hits") is { } hits && hits != "0" && r.GetAttribute("number") is { } line
                    && seen.Add(file + ":" + line))
                {
                    w.Write(file);
                    w.Write(':');
                    w.WriteLine(line);
                }
            }
        }
        if (Environment.GetEnvironmentVariable("ARENA_COVER_KEEP") != "1")
        {
            File.Delete(xml);
        }
    }

    private static void Snapshot(string output)
    {
        using var p = Process.Start(new ProcessStartInfo("dotnet", ["tool", "run", "dotnet-coverage", "snapshot", Session!, "--reset", "--nologo", "-o", output])
        {
            WorkingDirectory = "/repo", RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var said = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            File.AppendAllText(Path.Combine(Dir!, "snapshot-errors.log"), $"{output}: exit {p.ExitCode}: {said}\n");
        }
    }
}
