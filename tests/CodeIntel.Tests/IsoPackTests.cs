// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using CodeIntel.Packs;
using CodeIntel.Packs.Sources;

namespace CodeIntel.Tests;

/// <summary>
/// The iso pack, from a few records of ISO Open Data as tools/iso-catalog.py writes them (tests/fixtures/code-intel/packs/iso,
/// written from tests/fixtures/code-intel/iso-sources): ISO/IEC 27001's editions, ISO 9001:2015 and 2026, ISO 9002's draft.
/// </summary>
[Collection("process-state")]
public class IsoPackTests
{
    static string Root => Path.Combine(AppContext.BaseDirectory, "packfixtures", "iso");

    [Fact]
    public void Each_deliverable_is_a_page_with_its_status_and_successor_linked_to_iso_org()
    {
        var docs = new IsoCatalog().IterDocs(Root).ToDictionary(d => d.Path);
        var old = docs["withdrawn/62085-iso-9001-2015.md"];
        Assert.Equal("ISO 9001:2015 Quality management systems — Requirements", old.Title);
        Assert.Equal("https://www.iso.org/standard/62085.html", old.Url);
        Assert.Contains("Status: Withdrawn, stage 95.99", old.Body);
        Assert.Contains("Replaced by: ISO 9001:2026 (current).", old.Body);
        Assert.Contains("ODC-By 1.0", old.Body);
        Assert.Contains("docs/compliance/frameworks/iso27001.md", docs["current/82875-iso-iec-27001-2022.md"].Body);
        Assert.Contains("- ISO/IEC 27001:2022/Amd 1:2024 (current)", docs["current/82875-iso-iec-27001-2022.md"].Body);

        var symbols = new IsoCatalog().IterSymbols(Root).ToList();
        var undated = symbols.Single(s => s.Name == "ISO/IEC 27001");
        Assert.Equal(("is", "ISO/IEC JTC 1/SC 27", "current/82875-iso-iec-27001-2022.md"), (undated.Kind, undated.Namespace, undated.DocPath));
        Assert.StartsWith("ISO/IEC 27001:2022 (current): Information security", undated.Signature);
        Assert.Equal("ics/35.030.md", symbols.Single(s => s.Name == "ICS 35.030").DocPath);
        Assert.Equal("committee", symbols.Single(s => s.Name == "ISO/TC 176/SC 2").Kind);
    }

    [Fact]
    public void An_iso_pack_answers_by_number_with_or_without_its_year_and_keeps_a_search_to_a_status()
    {
        using var dir = new TempDir();
        var outPath = Path.Combine(dir.Path, "iso.kpack");
        PackBuilder.BuildPack(new IsoCatalog(), Root, outPath, "1", FakeEmbedder.Embed, log: TextWriter.Null);
        var packs = PackStore.OpenPacks([outPath]);
        try
        {
            var meta = packs[0].Meta;
            Assert.Equal("deliverables=1922cfb1da4a,committees=04cd74fca741,ics=a5f593caeb09", meta["source_commit"]);
            Assert.Equal("ODC-By-1.0", meta["license"]);
            Assert.Contains("ISO Open Data", meta["attribution"]);
            Assert.Equal("0", meta["unresolved_symbol_count"]);
            Assert.Equal("current=current/;withdrawn=withdrawn/;development=development/;cancelled=cancelled/;committees=committees/;ics=ics/", meta["facets"]);

            string Url(string name) => PackStore.LookupSymbol(packs, name).First()["url"]!.GetValue<string>();
            Assert.Equal("https://www.iso.org/standard/82875.html", Url("ISO/IEC 27001:2022"));
            Assert.Equal("https://www.iso.org/standard/82875.html", Url("ISO 27001"));
            Assert.Equal("https://www.iso.org/standard/82875.html", Url("iso/iec 27001"));
            Assert.Equal("https://www.iso.org/standard/88464.html", Url("ISO 9001"));
            Assert.Equal("https://www.iso.org/standard/62085.html", Url("ISO 9001:2015"));
            Assert.Empty(PackStore.LookupSymbol(packs, "ISO 9001:2015", "current"));

            var withdrawn = PackStore.SearchText(packs, "quality management", "withdrawn");
            Assert.NotEmpty(withdrawn);
            Assert.All(withdrawn, r => Assert.StartsWith("withdrawn/", r["doc_path"]!.GetValue<string>()));
            var semantic = PackStore.SearchDocs(packs, FakeEmbedder.Vector("information security management"), "current", limit: 5);
            Assert.All(semantic, r => Assert.StartsWith("current/", r["doc_path"]!.GetValue<string>()));
            Assert.Single(PackStore.SelectPacks(packs, "iso"));
        }
        finally { PackStore.ClosePacks(packs); }
    }
}
