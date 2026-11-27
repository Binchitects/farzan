// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using CodeIntel.Packs;
using CodeIntel.Packs.Sources;

namespace CodeIntel.Tests;

/// <summary>
/// The commoncriteria pack, from the pages tools/cc-texts.py writes from an excerpt of the CC's XML and PPs written for the
/// tests (tests/fixtures/code-intel/packs/commoncriteria, from tests/fixtures/code-intel/cc-sources).
/// </summary>
[Collection("process-state")]
public class CommonCriteriaPackTests
{
    static string Root => Path.Combine(AppContext.BaseDirectory, "packfixtures", "commoncriteria");

    [Fact]
    public void A_component_is_a_page_with_its_definition_and_the_pps_that_require_it()
    {
        var docs = new CommonCriteriaTexts().IterDocs(Root).ToDictionary(d => d.Path);
        var uau = docs["components/FIA/FIA_UAU.2.md"];
        Assert.Equal("FIA_UAU.2 User authentication before any action", uau.Title);
        Assert.Contains("## CC:2022", uau.Body);
        Assert.Contains("## CC 3.1 Revision 5", uau.Body);
        Assert.Contains("| Protection Profile for Example Devices, Version 1.0 (PP_EX_V1.0) | US | mandatory | FIA_UAU.2.1 |", uau.Body);
        Assert.Contains("TD9001", uau.Body);

        // A PP whose notice restricts reproduction: its page of metadata and requirements, none of its text.
        Assert.Equal(["pp/pp9999b/index.md"], docs.Keys.Where(p => p.StartsWith("pp/pp9999b/")));
        Assert.Contains("| FDP_ACC.1 | mandatory | FDP_ACC.1.1 |", docs["pp/pp9999b/index.md"].Body);

        var symbols = new CommonCriteriaTexts().IterSymbols(Root).ToList();
        var comp = symbols.Single(s => s.Name == "FIA_UAU.2");
        Assert.Equal(("component", "Common Criteria", "components/FIA/FIA_UAU.2.md"), (comp.Kind, comp.Namespace, comp.DocPath));
        Assert.Contains("required by 2 PPs", comp.Signature);
        Assert.Contains(symbols, s => s.Name == "PP_EX_V1.0" && s.Kind == "protection-profile");
        Assert.Contains(symbols, s => s.Name == "BSI-CC-PP-9999-2026");
        Assert.Contains(symbols, s => s.Name == "TD9001" && s.Kind == "technical-decision");
        Assert.Contains(symbols, s => s.Name == "EAL4" && s.Kind == "package");
    }

    [Fact]
    public void A_commoncriteria_pack_answers_which_pps_require_a_component_and_keeps_a_search_to_a_part()
    {
        using var dir = new TempDir();
        var outPath = Path.Combine(dir.Path, "commoncriteria.kpack");
        PackBuilder.BuildPack(new CommonCriteriaTexts(), Root, outPath, "1", FakeEmbedder.Embed, log: TextWriter.Null);
        var packs = PackStore.OpenPacks([outPath]);
        try
        {
            var meta = packs[0].Meta;
            Assert.StartsWith("files=5,tds=1,sha256=", meta["source_commit"]);
            Assert.Equal("0", meta["unresolved_symbol_count"]);
            Assert.Equal("components=components/;cc2022=cc2022/;cem2022=cem2022/;cc31=cc31/;cem31=cem31/;ccra=ccra/;pp=pp/;sd=sd/;td=td/", meta["facets"]);
            Assert.Contains("17 U.S.C. 105", meta["attribution"]);

            var hit = PackStore.LookupSymbol(packs, "FIA_UAU.2").First();
            Assert.Equal("components/FIA/FIA_UAU.2.md", hit["doc_path"]!.GetValue<string>());
            Assert.Empty(PackStore.LookupSymbol(packs, "FIA_UAU.2", "td"));
            var text = PackStore.SearchText(packs, "administrators", "td");
            Assert.NotEmpty(text);
            Assert.All(text, r => Assert.StartsWith("td/", r["doc_path"]!.GetValue<string>()));
            var semantic = PackStore.SearchDocs(packs, FakeEmbedder.Vector("authenticate before any action"), "components", limit: 5);
            Assert.All(semantic, r => Assert.StartsWith("components/", r["doc_path"]!.GetValue<string>()));
            Assert.Single(PackStore.SelectPacks(packs, "commoncriteria"));
        }
        finally { PackStore.ClosePacks(packs); }
    }
}
