// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Globalization;
using System.Xml.Linq;

namespace Llm.Api.Decks;

/// <summary>A slide read from a PowerPoint file: what the deck holds of it, and its first picture's bytes (to keep as the person's file).</summary>
public sealed record ReadSlide(DeckSlide Slide, DeckImage? Picture);

public static partial class DeckPptx
{
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    /// <summary>
    /// A PowerPoint file read into slides, in its order: each slide's title, subtitle, points (its other words), table,
    /// chart (its numbers from the chart's own cache), first picture and speaker notes, on the layout that shows them; each
    /// marked with the slide it came from, so <see cref="Revise"/> keeps the slides no one changed exactly as they were.
    /// </summary>
    public static (string Title, List<ReadSlide> Slides) Read(byte[] pptx)
    {
        var package = new Package(Unzip(pptx));
        var slides = new List<ReadSlide>();
        var number = 0;
        foreach (var path in package.SlidesInOrder().Select(s => s.Target))
        {
            number++;
            if (package.Xml(path)?.Root is not { } root)
            {
                continue;
            }
            var slide = new DeckSlide { Layout = "bullets" };
            var points = new List<List<string>>();
            DeckImage? picture = null;
            foreach (var shape in root.Descendants(P + "sp"))
            {
                var ph = shape.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
                var type = (string?)ph?.Attribute("type");
                var lines = shape.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim()).Where(l => l.Length > 0).ToList();
                if (lines.Count == 0 || type is "sldNum" or "dt" or "ftr")
                {
                    continue;
                }
                switch (type)
                {
                    case "title" or "ctrTitle":
                        slide.Title ??= string.Join(" ", lines);
                        if (type == "ctrTitle")
                        {
                            slide.Layout = "title";
                        }
                        break;
                    case "subTitle":
                        slide.Subtitle ??= string.Join(" ", lines);
                        break;
                    default:
                        points.Add(lines);
                        break;
                }
            }
            foreach (var frame in root.Descendants(P + "graphicFrame"))
            {
                if (frame.Descendants(A + "tbl").FirstOrDefault() is { } tbl)
                {
                    var rows = tbl.Elements(A + "tr").Select(tr => tr.Elements(A + "tc").Select(tc => string.Concat(tc.Descendants(A + "t").Select(t => t.Value)).Trim()).ToList()).ToList();
                    if (rows.Count > 0 && slide.Table is null)
                    {
                        slide.Table = new DeckTable { Columns = rows[0], Rows = [.. rows.Skip(1)] };
                    }
                }
                else if (frame.Descendants(C + "chart").FirstOrDefault()?.Attribute(R + "id")?.Value is { } rel
                    && package.Rels(path).FirstOrDefault(r => r.Id == rel).Target is { } chartPath && package.Xml(chartPath) is { } chart && slide.Chart is null)
                {
                    slide.Chart = ReadChart(chart);
                }
            }
            if (root.Descendants(P + "pic").FirstOrDefault()?.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed")?.Value is { } embed
                && package.Rels(path).FirstOrDefault(r => r.Id == embed).Target is { } media && package.Bytes(media) is { } bytes)
            {
                var kind = Path.GetExtension(media).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", _ => null };
                if (kind is not null)
                {
                    picture = new DeckImage(bytes, kind);
                    slide.Picture = new DeckPicture { Prompt = slide.Title ?? "" };
                }
            }
            if (package.Target(path, "notesSlide") is { } notesPath && package.Xml(notesPath) is { } notes)
            {
                var said = notes.Descendants(P + "sp").Where(s => (string?)s.Descendants(P + "ph").FirstOrDefault()?.Attribute("type") == "body")
                    .SelectMany(s => s.Descendants(A + "p")).Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim()).Where(l => l.Length > 0);
                slide.Notes = string.Join("\n", said) is { Length: > 0 } n ? n : null;
            }
            // The layout that shows what it holds.
            var layoutType = package.Target(path, "slideLayout") is { } layoutPath ? (string?)package.Xml(layoutPath)?.Root?.Attribute("type") : null;
            if (slide.Layout != "title")
            {
                if (slide.Table is not null)
                {
                    slide.Layout = "table";
                }
                else if (slide.Chart is not null)
                {
                    slide.Layout = "chart";
                }
                else if (points.Count >= 2 && layoutType is "twoObj" or "twoTxTwoObj")
                {
                    slide.Layout = "two";
                    slide.Left = points[0];
                    slide.Right = points[1];
                    points = [];
                }
                else if (layoutType == "secHead")
                {
                    slide.Layout = "section";
                    slide.Subtitle ??= points.Count > 0 ? string.Join(" ", points[0]) : null;
                    points = [];
                }
                else if (slide.Picture is not null && points.Count == 0)
                {
                    slide.Layout = "picture";
                }
            }
            else if (points.Count > 0)
            {
                slide.Subtitle ??= string.Join(" ", points[0]);
                points = [];
            }
            if (points.Count > 0)
            {
                slide.Bullets = [.. points.SelectMany(p => p)];
            }
            slide.Origin = new DeckOrigin { Slide = number };
            slides.Add(new ReadSlide(slide, picture));
        }
        var title = package.Xml("docProps/core.xml")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value is { Length: > 0 } t ? t
            : slides.Select(s => s.Slide.Title).FirstOrDefault(x => x is { Length: > 0 }) ?? "A deck";
        return (title, slides);
    }

    /// <summary>A chart's kind and numbers, from the values the chart part keeps with it.</summary>
    private static DeckChart ReadChart(XDocument chart)
    {
        var plot = chart.Descendants(C + "plotArea").Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal));
        var name = plot?.Name.LocalName;
        var type = name switch
        {
            "lineChart" => "line",
            "areaChart" => "area",
            "pieChart" or "pie3DChart" => "pie",
            "doughnutChart" => "doughnut",
            "scatterChart" => "scatter",
            "barChart" or "bar3DChart" => plot!.Element(C + "grouping")?.Attribute("val")?.Value is "stacked" or "percentStacked" ? "stacked"
                : plot.Element(C + "barDir")?.Attribute("val")?.Value == "bar" ? "hbar" : "bar",
            _ => "bar",
        };
        static List<(int, string)> Points(XElement? e) => e?.Descendants(C + "pt")
            .Select(p => (int.TryParse((string?)p.Attribute("idx"), CultureInfo.InvariantCulture, out var i) ? i : 0, p.Element(C + "v")?.Value ?? "")).ToList() ?? [];
        var series = plot?.Elements(C + "ser").ToList() ?? [];
        var first = series.FirstOrDefault();
        var categories = Points(first?.Element(C + "cat") ?? first?.Element(C + "xVal"));
        var count = categories.Count > 0 ? categories.Max(c => c.Item1) + 1 : 0;
        var names = new string[count];
        foreach (var (i, v) in categories)
        {
            names[i] = v;
        }
        return new DeckChart
        {
            Type = type,
            Categories = [.. names.Select(n => n ?? "")],
            Series = [.. series.Select(s =>
            {
                var values = new double?[count];
                foreach (var (i, v) in Points(s.Element(C + "val") ?? s.Element(C + "yVal")))
                {
                    if (i < count && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        values[i] = d;
                    }
                }
                return new DeckSeries { Name = string.Concat(s.Element(C + "tx")?.Descendants(C + "v").Select(v => v.Value) ?? []) is { Length: > 0 } n ? n : "Values", Values = [.. values] };
            })],
        };
    }

    /// <summary>
    /// A PowerPoint file revised: the slides no one changed (still as <see cref="Read"/> read them) kept exactly as they were,
    /// with their pictures, animations and look; the changed and the new made on the file's own layouts; the removed gone;
    /// all in the deck's order. Its sections are made again from the deck's section slides.
    /// </summary>
    public static byte[] Revise(byte[] source, string title, List<DeckSlide> slides, IReadOnlyDictionary<Guid, DeckImage> images, DateTimeOffset at, DeckLook? look = null)
    {
        look = DeckModel.Clean(look);
        var package = new Package(Unzip(source));
        var original = package.SlidesInOrder();
        var keep = new HashSet<int>(slides.Where(DeckModel.Unchanged).Select(s => s.Origin!.Slide));
        // The originals not kept, and their notes and charts, out; the list of slides made again below.
        for (var i = 0; i < original.Count; i++)
        {
            if (!keep.Contains(i + 1))
            {
                package.RemoveSlide(original[i].Id, original[i].Target);
            }
        }
        package.ClearSlideList();
        var layouts = package.Layouts();
        var (width, height) = package.SlideSize();
        var notesMaster = package.Target("ppt/presentation.xml", "notesMaster");
        var ids = new List<long>();
        var made = 0;
        for (var n = 0; n < slides.Count; n++)
        {
            var slide = slides[n];
            if (DeckModel.Unchanged(slide) && slide.Origin!.Slide <= original.Count)
            {
                ids.Add(package.ListSlide(original[slide.Origin.Slide - 1].Id));
                continue;
            }
            string path;
            do
            {
                path = $"ppt/slides/deckSlide{++made}.xml";
            }
            while (package.Has(path));
            var writer = new SlideWriter(package, n + 1, slides.Count, width, height, look);
            package.Set(path, writer.Slide(slide, images));
            package.Override(path, CtBase + "presentationml.slide+xml");
            writer.Relate("slideLayout", "../slideLayouts/" + Path.GetFileName(Choose(layouts, slide.Layout).Path));
            if (notesMaster is not null && slide.Notes is { Length: > 0 } notes)
            {
                var notesPath = path.Replace("slides/deckSlide", "notesSlides/deckNotes", StringComparison.Ordinal);
                package.Set(notesPath, Notes(notes));
                package.Override(notesPath, CtBase + "presentationml.notesSlide+xml");
                package.SetRels(notesPath, [("rId1", "notesMaster", "../notesMasters/" + Path.GetFileName(notesMaster)), ("rId2", "slide", "../slides/" + Path.GetFileName(path))]);
                writer.Relate("notesSlide", "../notesSlides/" + Path.GetFileName(notesPath));
            }
            package.SetRels(path, writer.Relations);
            ids.Add(package.AddSlide("slides/" + Path.GetFileName(path)));
        }
        package.SetSections(title, slides, ids);
        package.SetTitle(title, at);
        return package.Zip();
    }
}
