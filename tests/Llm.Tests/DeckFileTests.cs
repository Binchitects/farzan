// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.IO.Compression;
using System.Xml.Linq;
using Llm.Api.Decks;

namespace Llm.Tests;

/// <summary>
/// The PowerPoint file with no app around it: every layout, chart and design builds a file whose parts are well-formed,
/// related and typed; tables, shapes, sections, links, transitions and slide numbers are PowerPoint's own; slides are
/// cleaned (crowded ones split, nothing lost) and words fitted to their boxes.
/// </summary>
public sealed class DeckFileTests
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Each XML part read (well-formed), every relationship's target in the file with its content type.</summary>
    private static Dictionary<string, XDocument> Checked(byte[] pptx)
    {
        using var zip = new ZipArchive(new MemoryStream(pptx));
        var parts = zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal))
            .ToDictionary(e => e.FullName, e => XDocument.Load(e.Open()));
        var types = parts["[Content_Types].xml"].Root!;
        foreach (var (path, rels) in parts.Where(x => x.Key.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var folder = path.Replace("_rels/", "", StringComparison.Ordinal)[..^".rels".Length];
            foreach (var rel in rels.Root!.Elements())
            {
                var target = rel.Attribute("Target")!.Value;
                if ((string?)rel.Attribute("TargetMode") == "External")
                {
                    Assert.EndsWith("/hyperlink", rel.Attribute("Type")!.Value, StringComparison.Ordinal);
                    continue;
                }
                var full = target.StartsWith('/') ? target[1..] : Path.GetFullPath(Path.Combine("/" + Path.GetDirectoryName(folder), target)).TrimStart('/');
                Assert.True(zip.GetEntry(full) is not null, $"{path} points at {full}, which is not in the file");
                var extension = Path.GetExtension(full).TrimStart('.');
                Assert.True(types.Elements().Any(t => (string?)t.Attribute("PartName") == "/" + full || string.Equals((string?)t.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)),
                    $"{full} has no content type");
            }
        }
        return parts;
    }

    private static DeckChart Numbers(string type) => new()
    {
        Type = type, Categories = ["1", "2", "3"], Series = [new DeckSeries { Name = "North", Values = [3, 5, 8] }, new DeckSeries { Name = "South", Values = [2, 4, null] }],
    };

    private static readonly Guid PictureId = Guid.Parse("01a1b3fb-0000-7000-8000-000000000001");
    private static readonly Dictionary<Guid, DeckImage> Pictures = new() { [PictureId] = new DeckImage(FakeModel.Png, "image/png") };

    private static List<DeckSlide> EveryLayout() => DeckModel.Clean(
    [
        new() { Layout = "title", Title = "The year", Subtitle = "What we did" },
        new() { Layout = "agenda", Title = "Agenda", Items = [new() { Title = "Results" }, new() { Title = "Plans", Text = "for next year" }] },
        new() { Layout = "section", Title = "Results" },
        new() { Layout = "bullets", Title = "Points", Bullets = ["One, at https://example.test/report.", "Two"] },
        new() { Layout = "two", Title = "Two", Left = ["L"], Right = ["R"] },
        new() { Layout = "comparison", Title = "Before and after", LeftTitle = "Before", Left = ["Slow"], RightTitle = "After", Right = ["Fast"] },
        new() { Layout = "table", Title = "Offices", Table = new() { Columns = ["Office", "People"], Rows = [["North", "120"], ["South", "80"]] } },
        new() { Layout = "stats", Title = "In numbers", Items = [new() { Value = "42%", Title = "Growth" }, new() { Value = "3", Title = "Offices" }] },
        new() { Layout = "cards", Title = "Teams", Items = [new() { Title = "Sales", Text = "Sells" }, new() { Title = "Ops", Text = "Runs" }] },
        new() { Layout = "section", Title = "Plans" },
        new() { Layout = "process", Title = "How", Items = [new() { Title = "Ask" }, new() { Title = "Build" }, new() { Title = "Ship" }] },
        new() { Layout = "timeline", Title = "When", Items = [new() { Title = "Q1", Text = "Start" }, new() { Title = "Q3", Text = "Done" }] },
        .. DeckModel.Charts.Select(c => new DeckSlide { Layout = "chart", Title = c, Chart = Numbers(c) }),
        new() { Layout = "picture", Title = "The office", Subtitle = "North", Picture = new() { Prompt = "an office", FileId = PictureId } },
        new() { Layout = "quote", Quote = "Less is more", By = "Mies" },
        new() { Layout = "closing", Title = "Thank you", Subtitle = "Questions?" },
    ]);

    [Fact]
    public void Every_layout_chart_and_design_makes_a_file_whose_parts_are_well_formed_related_and_typed()
    {
        var slides = EveryLayout();
        Assert.Equal(DeckModel.Layouts.Order(), slides.Select(s => s.Layout).Distinct().Order());
        foreach (var design in DeckModel.Designs)
        {
            var pptx = DeckPptx.Build("The year", slides, Pictures, null, DateTimeOffset.UnixEpoch,
                new DeckLook { Design = design, Footer = "Company, 2026", Numbers = true, Transition = "fade" });
            var parts = Checked(pptx);
            Assert.Equal(slides.Count, parts["ppt/presentation.xml"].Root!.Element(P + "sldIdLst")!.Elements().Count());
            // Its design's colours and fonts are the theme's.
            var theme = System.Text.Json.Nodes.JsonNode.Parse(DeckPptx.DesignTheme(design))!;
            Assert.Equal(theme["colors"]!["accent1"]!.GetValue<string>().TrimStart('#'),
                parts["ppt/theme/theme1.xml"].Descendants(A + "accent1").Single().Element(A + "srgbClr")!.Attribute("val")!.Value);
            // Kept beside the test's output, to look at (soffice --headless --convert-to pdf).
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, $"deck-{design}.pptx"), pptx);
        }
    }

    [Fact]
    public void Tables_shapes_charts_sections_links_transitions_and_numbers_are_PowerPoints_own()
    {
        var slides = EveryLayout();
        var parts = Checked(DeckPptx.Build("The year", slides, Pictures, null, DateTimeOffset.UnixEpoch,
            new DeckLook { Footer = "Company, 2026", Transition = "push" }));
        string Slide(string layout) => parts[$"ppt/slides/slide{slides.FindIndex(s => s.Layout == layout) + 1}.xml"].ToString();
        // A table with its header and rows, in PowerPoint's built-in style.
        var table = parts[$"ppt/slides/slide{slides.FindIndex(s => s.Layout == "table") + 1}.xml"].Descendants(A + "tbl").Single();
        Assert.Equal(3, table.Elements(A + "tr").Count());
        Assert.Contains("North", table.ToString(), StringComparison.Ordinal);
        // Steps as chevrons (the first a pointed tab), a timeline's dots, an agenda's numbered circles.
        Assert.Equal(["homePlate", "chevron", "chevron"], parts[$"ppt/slides/slide{slides.FindIndex(s => s.Layout == "process") + 1}.xml"]
            .Descendants(A + "prstGeom").Select(g => g.Attribute("prst")!.Value).Where(g => g is "homePlate" or "chevron"));
        Assert.Equal(2, parts[$"ppt/slides/slide{slides.FindIndex(s => s.Layout == "timeline") + 1}.xml"].Descendants(A + "prstGeom").Count(g => g.Attribute("prst")!.Value == "ellipse"));
        Assert.Contains("42%", Slide("stats"), StringComparison.Ordinal);
        // Each chart type as PowerPoint draws it.
        var charts = parts.Where(p => p.Key.StartsWith("ppt/charts/", StringComparison.Ordinal)).Select(p => p.Value.ToString()).ToList();
        foreach (var kind in new[] { "<c:barDir val=\"bar\"", "<c:grouping val=\"stacked\"", "c:lineChart", "c:areaChart", "c:pieChart", "c:doughnutChart", "c:scatterChart" })
        {
            Assert.Contains(charts, c => c.Contains(kind, StringComparison.Ordinal));
        }
        // A pie shows one series, each slice its colour, as shares.
        var pie = charts.Single(c => c.Contains("c:pieChart", StringComparison.Ordinal));
        Assert.Single(XDocument.Parse(pie).Descendants(), e => e.Name.LocalName == "ser");
        Assert.Contains("<c:showPercent val=\"1\"", pie, StringComparison.Ordinal);
        // Sections start at each section slide; the slides before the first are in one named by the deck.
        XNamespace p14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
        var sections = parts["ppt/presentation.xml"].Descendants(p14 + "section").ToList();
        Assert.Equal(["The year", "Results", "Plans"], sections.Select(s => s.Attribute("name")!.Value));
        Assert.Equal(slides.Count, sections.Sum(s => s.Descendants(p14 + "sldId").Count()));
        // A web address is a link; the footer and the number on every slide but the first and the last; the transition on each.
        var points = slides.FindIndex(s => s.Layout == "bullets") + 1;
        Assert.Contains(parts[$"ppt/slides/_rels/slide{points}.xml.rels"].Root!.Elements(), r => r.Attribute("Target")!.Value == "https://example.test/report");
        Assert.Contains("<a:hlinkClick", Slide("bullets"), StringComparison.Ordinal);
        Assert.Contains($"{points} / {slides.Count}", Slide("bullets"), StringComparison.Ordinal);
        Assert.Contains("Company, 2026", Slide("bullets"), StringComparison.Ordinal);
        Assert.DoesNotContain("Company, 2026", Slide("title"), StringComparison.Ordinal);
        Assert.DoesNotContain(" / ", Slide("closing"), StringComparison.Ordinal);
        Assert.All(Enumerable.Range(1, slides.Count), n => Assert.Contains("<p:push", parts[$"ppt/slides/slide{n}.xml"].ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void A_crowded_slide_is_split_and_nothing_is_lost_and_what_a_layout_cannot_show_is_left_out()
    {
        var points = Enumerable.Range(1, 14).Select(i => $"Point {i}").ToList();
        var slides = DeckModel.Clean([new DeckSlide { Layout = "bullets", Title = "Many", Bullets = points, Notes = "Say it" }]);
        Assert.Equal(["Many", "Many (continued)", "Many (continued)"], slides.Select(s => s.Title));
        Assert.Equal(points, slides.SelectMany(s => s.Bullets!));
        Assert.All(slides, s => Assert.True(s.Bullets!.Count <= DeckModel.MostPoints));
        Assert.Equal("Say it", slides[0].Notes);
        Assert.Null(slides[1].Notes);
        // An agenda written as points is its items; a layout of items with none says its words as points.
        Assert.Equal(["A", "B"], DeckModel.Clean(new DeckSlide { Layout = "agenda", Bullets = ["A", "B"] }).Items!.Select(i => i.Title));
        Assert.Equal("bullets", DeckModel.Clean(new DeckSlide { Layout = "stats", Bullets = ["x"] }).Layout);
        Assert.Equal(4, DeckModel.Clean(new DeckSlide { Layout = "stats", Items = [.. Enumerable.Range(1, 9).Select(i => new DeckItem { Value = $"{i}" })] }).Items!.Count);
        // A table with no columns is none; its rows are as wide as its columns.
        Assert.Equal("bullets", DeckModel.Clean(new DeckSlide { Layout = "table", Table = new() { Rows = [["a"]] } }).Layout);
        Assert.Equal([["a", ""]], DeckModel.Clean(new DeckSlide { Layout = "table", Table = new() { Columns = ["A", "B"], Rows = [["a"]] } }).Table!.Rows);
        // A pie has one series; an unknown chart is bars; an unknown look is the built-in one.
        Assert.Single(DeckModel.Clean(new DeckSlide { Layout = "chart", Chart = Numbers("pie") }).Chart!.Series);
        Assert.Equal("bar", DeckModel.Clean(new DeckSlide { Layout = "chart", Chart = Numbers("radar") }).Chart!.Type);
        var look = DeckModel.Clean(new DeckLook { Design = "neon", Transition = "spin", Footer = new string('x', 500) });
        Assert.Equal(("arena", "none", 120), (look.Design, look.Transition, look.Footer!.Length));
    }

    [Fact]
    public void Words_on_a_colour_are_the_themes_dark_or_light_whichever_reads_on_it()
    {
        var slides = DeckModel.Clean([new DeckSlide { Layout = "process", Title = "How", Items = [new() { Title = "Ask" }, new() { Title = "Build" }, new() { Title = "Ship" }] },
            new DeckSlide { Layout = "table", Title = "T", Table = new() { Columns = ["A"], Rows = [["a"], ["b"]] } }]);
        // The colour of the first words in each of a slide's step shapes (chevrons) or table cells.
        string Colours(string design, int slide, Func<XElement, bool> which)
        {
            var parts = Checked(DeckPptx.Build("T", slides, Pictures, null, DateTimeOffset.UnixEpoch, new DeckLook { Design = design, Numbers = false }));
            return string.Join(",", parts[$"ppt/slides/slide{slide}.xml"].Descendants().Where(which)
                .Select(e => e.Descendants(A + "rPr").First().Descendants(A + "schemeClr").First().Attribute("val")!.Value));
        }
        static bool Step(XElement e) => e.Name.LocalName == "sp" && e.Descendants(A + "prstGeom").Any(g => g.Attribute("prst")!.Value is "homePlate" or "chevron");
        // Bold's steps (red, teal, yellow) all read best in its dark words; Minimal's dark grey in its white paper.
        Assert.Equal("tx1,tx1,tx1", Colours("bold", 1, Step));
        Assert.Equal("bg1", Colours("minimal", 1, Step).Split(',')[0]);
        // Midnight's table: the header in its dark paper's colour on the light blue, the rows in its light words.
        Assert.Equal("bg1,tx1,tx1", Colours("midnight", 2, e => e.Name == A + "tc"));
    }

    [Theory]
    [InlineData(3, 1.0)]
    [InlineData(12, 0.85)]
    [InlineData(60, 0.5)]
    public void Words_too_many_for_their_box_are_made_smaller_never_below_half(int lines, double atMost)
    {
        // The built-in body box: 10.7 by 4.7 million EMU, 22 points.
        var text = Enumerable.Repeat("A point of about sixty letters, as a slide's point may be long.", lines).ToList();
        var scale = DeckPptx.FitScale(text, 10728960, 4663440, 22);
        Assert.InRange(scale, 0.5, atMost);
        if (lines == 3)
        {
            Assert.Equal(1, scale);
        }
    }

    [Fact]
    public void An_outline_is_made_into_slides_as_written()
    {
        const string Outline = """
            # The year
            What we did, and what is next

            ## Agenda
            1. Results
            2. Plans: for next year

            ## Results
            - Sales grew
            - Costs fell
            Notes: Thank the teams.
            Then the numbers.

            ## Offices
            | Office | People |
            |---|---|
            | North | 120 |

            ## How we work
            1. Ask
            2. Build: in small steps

            > Less is more
            — Mies

            ## Next
            """;
        Assert.True(DeckOutline.Looks(Outline));
        Assert.False(DeckOutline.Looks("A deck about our year, with the numbers."));
        var (title, slides) = DeckOutline.Parse(Outline);
        Assert.Equal("The year", title);
        Assert.Equal(["title", "agenda", "bullets", "table", "process", "section"], slides.Select(x => x.Layout));
        Assert.Equal("What we did, and what is next", slides[0].Subtitle);
        Assert.Equal(("Plans", "for next year"), (slides[1].Items![1].Title!, slides[1].Items![1].Text!));
        Assert.Equal(["Sales grew", "Costs fell"], slides[2].Bullets);
        Assert.Equal("Thank the teams.\nThen the numbers.", slides[2].Notes);
        Assert.Equal([["North", "120"]], slides[3].Table!.Rows);
        // A quote under steps stays with them: steps are what that slide shows.
        Assert.Equal("Less is more", slides[4].Quote);
    }

    [Fact]
    public void A_file_read_and_revised_keeps_the_slides_no_one_changed_exactly_as_they_were()
    {
        // A file of PowerPoint's kind (ours, in another design): a title, points with a picture, a table, a chart, notes.
        var made = DeckModel.Clean(
        [
            new() { Layout = "title", Title = "The year", Subtitle = "What we did", Notes = "Welcome." },
            new() { Layout = "bullets", Title = "Points", Bullets = ["One", "Two"], Picture = new() { Prompt = "x", FileId = PictureId } },
            new() { Layout = "table", Title = "Offices", Table = new() { Columns = ["Office", "People"], Rows = [["North", "120"]] } },
            new() { Layout = "chart", Title = "Sales", Chart = Numbers("line") },
        ]);
        var original = DeckPptx.Build("The year", made, Pictures, null, DateTimeOffset.UnixEpoch, new DeckLook { Design = "paper", Numbers = false });
        var (title, read) = DeckPptx.Read(original);
        Assert.Equal("The year", title);
        Assert.Equal(["title", "bullets", "table", "chart"], read.Select(r => r.Slide.Layout));
        Assert.Equal("What we did", read[0].Slide.Subtitle);
        Assert.Equal("Welcome.", read[0].Slide.Notes);
        Assert.Equal(["One", "Two"], read[1].Slide.Bullets);
        Assert.Equal(FakeModel.Png, read[1].Picture!.Data);
        Assert.Equal([["North", "120"]], read[2].Slide.Table!.Rows);
        Assert.Equal(("line", 2, 8d), (read[3].Slide.Chart!.Type, read[3].Slide.Chart!.Series.Count, read[3].Slide.Chart!.Series[0].Values[2]!.Value));

        // As the import keeps them: cleaned, each with what it was when read.
        var slides = read.Select(r => DeckModel.Clean(r.Slide)).ToList();
        foreach (var x in slides)
        {
            x.Origin!.Read = DeckModel.Fingerprint(x);
        }
        Assert.All(slides, x => Assert.True(DeckModel.Unchanged(x)));
        // The person changes the table, removes the points, moves the chart first and adds a closing slide.
        slides[2].Table!.Rows[0][1] = "125";
        Assert.False(DeckModel.Unchanged(slides[2]));
        List<DeckSlide> revised = [slides[3], slides[0], slides[2], new DeckSlide { Layout = "closing", Title = "Thanks" }];
        var file = DeckPptx.Revise(original, "The year", revised, Pictures, DateTimeOffset.UnixEpoch);
        var parts = Checked(file);
        using var before = new ZipArchive(new MemoryStream(original));
        using var after = new ZipArchive(new MemoryStream(file));
        string Text(ZipArchive z, string path) => new StreamReader(z.GetEntry(path)!.Open()).ReadToEnd();
        // In the deck's order; the unchanged chart and title slides are the same parts, byte for byte, the chart's too.
        var order = parts["ppt/presentation.xml"].Root!.Element(P + "sldIdLst")!.Elements().Select(e => e.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!.Value)
            .Select(id => parts["ppt/_rels/presentation.xml.rels"].Root!.Elements().Single(r => r.Attribute("Id")!.Value == id).Attribute("Target")!.Value).ToList();
        Assert.Equal(["slides/slide4.xml", "slides/slide1.xml"], order[..2]);
        Assert.Equal(Text(before, "ppt/slides/slide4.xml"), Text(after, "ppt/slides/slide4.xml"));
        Assert.Equal(Text(before, "ppt/slides/slide1.xml"), Text(after, "ppt/slides/slide1.xml"));
        Assert.Equal(Text(before, "ppt/charts/deckChart1.xml"), Text(after, "ppt/charts/deckChart1.xml"));
        // The changed table is made anew with its new number; the removed slide and its picture's slide are gone; the file's design stays.
        Assert.Contains("125", Text(after, "ppt/slides/" + Path.GetFileName(order[2])), StringComparison.Ordinal);
        Assert.Null(after.GetEntry("ppt/slides/slide2.xml"));
        Assert.Null(after.GetEntry("ppt/slides/slide3.xml"));
        Assert.Equal(4, order.Count);
        Assert.Contains("Georgia", Text(after, "ppt/theme/theme1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_deck_written_as_Markdown_reads_back_to_the_same_slides()
    {
        var slides = EveryLayout();
        slides[3].Notes = "Say this.\n\nThen that.";
        slides[3].Origin = new DeckOrigin { Slide = 4, Read = "0123456789ABCDEF0123456789ABCDEF" };
        slides[6].Table!.Rows[0][0] = "North | East";
        var text = DeckOutline.Write("The year", slides);
        var (title, read) = DeckOutline.Parse(text);
        Assert.Equal("The year", title);
        Assert.Equal(DeckModel.Write(slides), DeckModel.Write(read));
        // And a deck whose name is not its first slide's says it before any slide.
        Assert.Equal("Board pack", DeckOutline.Parse(DeckOutline.Write("Board pack", slides)).Title);
    }
}
