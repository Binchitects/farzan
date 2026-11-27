// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Llm.Api.Decks;

/// <summary>A chart's numbers: from the person's words, or asked of a database (<see cref="Ask"/>) and filled in here.</summary>
public sealed class DeckChart
{
    /// <summary>bar, hbar (bars across), stacked, line, area, pie, doughnut or scatter (its categories the x numbers).</summary>
    public string Type { get; set; } = "bar";
    public List<string> Categories { get; set; } = [];
    public List<DeckSeries> Series { get; set; } = [];
    /// <summary>The question asked of a database for its numbers, run again with Refresh.</summary>
    public DeckAsk? Ask { get; set; }
    /// <summary>Where its numbers came from, or why there are none.</summary>
    public string? Note { get; set; }
}

public sealed class DeckSeries
{
    public string Name { get; set; } = "";
    public List<double?> Values { get; set; } = [];
}

public sealed class DeckAsk
{
    /// <summary>The database's name, as Data lists it.</summary>
    public string Source { get; set; } = "";
    public string Question { get; set; } = "";
}

/// <summary>A picture: made by the picture model from its prompt, or a file of the person's.</summary>
public sealed class DeckPicture
{
    public string Prompt { get; set; } = "";
    public Guid? FileId { get; set; }
}

/// <summary>One of a slide's items: an agenda's or timeline's step, a process step, a card, a big number.</summary>
public sealed class DeckItem
{
    /// <summary>Its heading: a step's name, a card's title, a number's label, a timeline's date.</summary>
    public string? Title { get; set; }
    public string? Text { get; set; }
    /// <summary>A big number's figure, as written ("42%", "3.2M").</summary>
    public string? Value { get; set; }
}

/// <summary>Where a slide read from a PowerPoint file came from: its number there, and what it was as read (to tell it unchanged).</summary>
public sealed class DeckOrigin
{
    public int Slide { get; set; }
    public string? Read { get; set; }
}

/// <summary>A table: its column names and rows of cells.</summary>
public sealed class DeckTable
{
    public List<string> Columns { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
}

/// <summary>A deck's look: a built-in design (when it has no company template), its footer, slide numbers and transition.</summary>
public sealed class DeckLook
{
    /// <summary>arena, midnight, paper, bold or minimal.</summary>
    public string Design { get; set; } = "arena";
    public string? Footer { get; set; }
    public bool Numbers { get; set; } = true;
    /// <summary>none, fade, push or wipe.</summary>
    public string Transition { get; set; } = "none";
}

/// <summary>One slide: its layout and what it holds.</summary>
public sealed class DeckSlide
{
    /// <summary>One of <see cref="DeckModel.Layouts"/>.</summary>
    public string Layout { get; set; } = "bullets";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string>? Bullets { get; set; }
    public List<string>? Left { get; set; }
    public List<string>? Right { get; set; }
    /// <summary>A comparison's two headings, over <see cref="Left"/> and <see cref="Right"/>.</summary>
    public string? LeftTitle { get; set; }
    public string? RightTitle { get; set; }
    /// <summary>An agenda's, timeline's, process's, cards' or big numbers' items.</summary>
    public List<DeckItem>? Items { get; set; }
    public DeckTable? Table { get; set; }
    public string? Quote { get; set; }
    public string? By { get; set; }
    public DeckChart? Chart { get; set; }
    public DeckPicture? Picture { get; set; }
    public string? Notes { get; set; }
    /// <summary>The slide of the PowerPoint file it was read from, when the deck is one opened to revise.</summary>
    public DeckOrigin? Origin { get; set; }
}

/// <summary>The slides as kept and sent, and the model's JSON made safe: known layouts, words cut to size, at most 30 slides.</summary>
public static class DeckModel
{
    public const int MostSlides = 30;
    /// <summary>The most points a slide shows; more go on a slide after it, its title continued.</summary>
    public const int MostPoints = 6;
    public static readonly string[] Layouts = ["title", "section", "agenda", "bullets", "two", "comparison", "chart", "table", "timeline", "process", "stats", "cards", "picture", "quote", "closing"];
    public static readonly string[] Charts = ["bar", "hbar", "stacked", "line", "area", "pie", "doughnut", "scatter"];
    public static readonly string[] Designs = ["arena", "midnight", "paper", "bold", "minimal"];
    public static readonly string[] Transitions = ["none", "fade", "push", "wipe"];

    /// <summary>How many items each layout of items shows at most.</summary>
    public static int MostItems(string layout) => layout switch { "stats" => 4, "cards" => 4, "process" => 5, "timeline" => 6, "agenda" => 8, _ => 0 };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Write(List<DeckSlide> slides) => JsonSerializer.Serialize(slides, Json);

    public static List<DeckSlide> Read(string json) => JsonSerializer.Deserialize<List<DeckSlide>>(json, Json) ?? [];

    private static string? Cut(string? s, int most)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > most ? t[..most] : t;
    }

    private static List<string>? Lines(List<string>? lines, int most = 8) =>
        lines?.Select(l => Cut(l, 300)).OfType<string>().Take(most).ToList() is { Count: > 0 } kept ? kept : null;

    /// <summary>A slide as it may be kept: a known layout, what that layout shows, nothing too long.</summary>
    public static DeckSlide Clean(DeckSlide s)
    {
        var layout = Layouts.Contains(s.Layout?.Trim().ToLowerInvariant()) ? s.Layout!.Trim().ToLowerInvariant() : "bullets";
        var chart = s.Chart is { } c ? CleanChart(c) : null;
        var picture = s.Picture is { } p && (Cut(p.Prompt, 1000) is not null || p.FileId is not null) ? new DeckPicture { Prompt = Cut(p.Prompt, 1000) ?? "", FileId = p.FileId } : null;
        // A chart slide with nothing to draw, or a picture slide with nothing to show, says its words instead.
        if (layout == "chart" && chart is null)
        {
            layout = "bullets";
        }
        if (layout == "picture" && picture is null)
        {
            layout = "bullets";
        }
        var items = s.Items?.Select(i => new DeckItem { Title = Cut(i.Title, 120), Text = Cut(i.Text, 300), Value = Cut(i.Value, 24) })
            .Where(i => i.Title is not null || i.Text is not null || i.Value is not null).ToList();
        // An agenda written as points is its items.
        if (layout == "agenda" && items is not { Count: > 0 } && Lines(s.Bullets, 8) is { } agenda)
        {
            items = [.. agenda.Select(a => new DeckItem { Title = a })];
        }
        var most = MostItems(layout);
        items = most > 0 && items is { Count: > 0 } ? [.. items.Take(most)] : null;
        if (most > 0 && items is null)
        {
            layout = "bullets";
        }
        var table = s.Table is { } t ? CleanTable(t) : null;
        if (layout == "table" && table is null)
        {
            layout = "bullets";
        }
        return new DeckSlide
        {
            Layout = layout,
            Title = Cut(s.Title, 200),
            Subtitle = Cut(s.Subtitle, 300),
            Bullets = Lines(s.Bullets, layout == "bullets" ? 30 : MostPoints),
            Left = Lines(s.Left, MostPoints),
            Right = Lines(s.Right, MostPoints),
            LeftTitle = Cut(s.LeftTitle, 120),
            RightTitle = Cut(s.RightTitle, 120),
            Items = items,
            Table = layout == "table" ? table : null,
            Quote = Cut(s.Quote, 500),
            By = Cut(s.By, 200),
            Chart = chart,
            Picture = picture,
            Notes = Cut(s.Notes, 4000),
            Origin = s.Origin is { Slide: > 0 } o ? new DeckOrigin { Slide = o.Slide, Read = Cut(o.Read, 64) } : null,
        };
    }

    private static DeckChart? CleanChart(DeckChart c)
    {
        var ask = c.Ask is { } a && Cut(a.Question, 1000) is { } q ? new DeckAsk { Source = Cut(a.Source, 100) ?? "", Question = q } : null;
        var categories = c.Categories.Select(x => Cut(x, 100) ?? "").Take(60).ToList();
        var series = c.Series.Where(x => x.Values.Count > 0).Take(6)
            .Select(x => new DeckSeries { Name = Cut(x.Name, 100) ?? "Values", Values = [.. x.Values.Take(categories.Count).Select(v => v is { } d && double.IsFinite(d) ? d : (double?)null)] })
            .ToList();
        // A chart with neither numbers nor a question is none, unless it says why it has none.
        if (ask is null && (categories.Count == 0 || series.Count == 0) && Cut(c.Note, 1000) is null)
        {
            return null;
        }
        var type = c.Type?.Trim().ToLowerInvariant() is { } k && Charts.Contains(k) ? k : "bar";
        // A pie or doughnut shows one series.
        if (type is "pie" or "doughnut")
        {
            series = [.. series.Take(1)];
        }
        return new DeckChart { Type = type, Categories = categories, Series = series, Ask = ask, Note = Cut(c.Note, 1000) };
    }

    private static DeckTable? CleanTable(DeckTable t)
    {
        var columns = t.Columns.Select(c => Cut(c, 80) ?? "").Take(8).ToList();
        if (columns.Count == 0)
        {
            return null;
        }
        var rows = t.Rows.Take(12).Select(r => r.Select(c => Cut(c, 200) ?? "").Take(columns.Count).Concat(Enumerable.Repeat("", columns.Count)).Take(columns.Count).ToList()).ToList();
        return rows.Count == 0 ? null : new DeckTable { Columns = columns, Rows = rows };
    }

    /// <summary>
    /// The slides as kept: each cleaned; a slide with more than <see cref="MostPoints"/> points split, the rest on slides
    /// after it (its title continued), so no slide is crowded and no point is lost; at most <see cref="MostSlides"/>.
    /// </summary>
    public static List<DeckSlide> Clean(IEnumerable<DeckSlide> slides)
    {
        var kept = new List<DeckSlide>();
        foreach (var slide in slides.Take(MostSlides).Select(Clean))
        {
            if (slide.Layout == "bullets" && slide.Bullets is { Count: > MostPoints } points)
            {
                for (var at = 0; at < points.Count; at += MostPoints)
                {
                    var part = Clean(slide);
                    part.Bullets = [.. points.Skip(at).Take(MostPoints)];
                    if (at > 0)
                    {
                        part.Title = slide.Title is { } t ? t + " (continued)" : null;
                        part.Picture = null;
                        part.Notes = null;
                        part.Origin = null;
                    }
                    kept.Add(part);
                }
            }
            else
            {
                kept.Add(slide);
            }
        }
        return [.. kept.Take(MostSlides)];
    }

    /// <summary>What a slide holds, as a short fingerprint (its origin left out): the same words, numbers and pictures, the same print.</summary>
    public static string Fingerprint(DeckSlide s)
    {
        var origin = s.Origin;
        s.Origin = null;
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s, Json))))[..32];
        }
        finally
        {
            s.Origin = origin;
        }
    }

    /// <summary>Whether a slide read from a PowerPoint file is still as it was read: kept as it is in the file.</summary>
    public static bool Unchanged(DeckSlide s) => s.Origin is { Read: { } read } && read == Fingerprint(s);

    /// <summary>A look as it may be kept: a known design and transition, a short footer.</summary>
    public static DeckLook Clean(DeckLook? look) => new()
    {
        Design = look?.Design?.Trim().ToLowerInvariant() is { } d && Designs.Contains(d) ? d : "arena",
        Footer = Cut(look?.Footer, 120),
        Numbers = look?.Numbers ?? true,
        Transition = look?.Transition?.Trim().ToLowerInvariant() is { } t && Transitions.Contains(t) ? t : "none",
    };

    public static string Write(DeckLook look) => JsonSerializer.Serialize(look, Json);

    public static DeckLook ReadLook(string? json) => string.IsNullOrWhiteSpace(json) ? new DeckLook() : Clean(JsonSerializer.Deserialize<DeckLook>(json, Json));
}
