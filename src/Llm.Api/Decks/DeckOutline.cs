// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Llm.Api.Decks;

/// <summary>
/// A deck as Markdown, both ways: what a person writes as an outline, and what a slides canvas holds (written by
/// <see cref="Write"/>, read back by <see cref="Parse"/> to the same slides). No model reads it.
/// <list type="bullet">
/// <item><c># Title</c> is a title slide, the lines under it its subtitle; <c>Title: …</c> before any heading names the deck.</item>
/// <item><c>## Heading {layout}</c> a slide (the layout said or read from what it holds); <c>Subtitle: …</c> its subtitle or caption.</item>
/// <item><c>- point</c>; <c>### Heading</c> a column (two columns, a comparison); <c>1. [42%] **Title** — words</c> an item
/// (an agenda's, a process's, a timeline's, a card, a figure; <c>1. Title: words</c> as people write it too).</item>
/// <item><c>| cells |</c> a table; <c>&gt; quote</c> and <c>— who</c>; <c>![prompt](file:id)</c> a picture; a fenced <c>chart</c> block its chart as JSON.</item>
/// <item><c>Notes:</c> starts what to say, to the next heading; <c>&lt;!-- slide 3 … --&gt;</c> where a slide came from in a file being revised.</item>
/// </list>
/// </summary>
public static partial class DeckOutline
{
    /// <summary>Whether words look like an outline (two headings or more), not a request in prose.</summary>
    public static bool Looks(string text) => Heading().IsMatch(text) && text.Split('\n').Count(l => l.TrimStart().StartsWith('#')) >= 2;

    /// <summary>The slides as Markdown that <see cref="Parse"/> reads back to the same slides.</summary>
    public static string Write(string title, IReadOnlyList<DeckSlide> slides)
    {
        var w = new StringBuilder();
        if (slides.Count == 0 || slides[0].Layout != "title" || slides[0].Title != title)
        {
            w.Append("Title: ").AppendLine(title).AppendLine();
        }
        foreach (var s in slides)
        {
            if (s.Layout == "title")
            {
                w.Append("# ").AppendLine(s.Title ?? "");
                if (s.Subtitle is { } sub)
                {
                    w.AppendLine(sub);
                }
            }
            else
            {
                w.Append("## ").Append(s.Title is { } t ? t + " " : "").Append('{').Append(s.Layout).AppendLine("}");
                if (s.Subtitle is { } sub)
                {
                    w.Append("Subtitle: ").AppendLine(sub);
                }
            }
            if (s.Origin is { } o)
            {
                w.Append("<!-- slide ").Append(o.Slide).Append(o.Read is { } r ? " " + r : "").AppendLine(" -->");
            }
            foreach (var b in s.Bullets ?? [])
            {
                w.Append("- ").AppendLine(b);
            }
            if (s.Layout is "two" or "comparison")
            {
                w.Append("###").AppendLine(s.LeftTitle is { } l ? " " + l : "");
                foreach (var b in s.Left ?? [])
                {
                    w.Append("- ").AppendLine(b);
                }
                w.Append("###").AppendLine(s.RightTitle is { } r ? " " + r : "");
                foreach (var b in s.Right ?? [])
                {
                    w.Append("- ").AppendLine(b);
                }
            }
            var n = 0;
            foreach (var i in s.Items ?? [])
            {
                w.Append(++n).Append(". ").Append(i.Value is { } v ? $"[{v}] " : "").Append("**").Append(i.Title ?? "").Append("**").AppendLine(i.Text is { } x ? " — " + x : "");
            }
            if (s.Table is { } table)
            {
                static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|", StringComparison.Ordinal))) + " |";
                w.AppendLine(Row(table.Columns)).AppendLine(Row(table.Columns.Select(_ => "---")));
                foreach (var r in table.Rows)
                {
                    w.AppendLine(Row(r));
                }
            }
            if (s.Quote is { } q)
            {
                w.Append("> ").AppendLine(q);
                if (s.By is { } by)
                {
                    w.Append("— ").AppendLine(by);
                }
            }
            if (s.Picture is { } p)
            {
                w.Append("![").Append(p.Prompt.Replace("]", ")", StringComparison.Ordinal)).Append("](").Append(p.FileId is { } f ? "file:" + f : "").AppendLine(")");
            }
            if (s.Chart is { } c)
            {
                w.AppendLine("```chart").AppendLine(JsonSerializer.Serialize(c, DeckModel.Json)).AppendLine("```");
            }
            if (s.Notes is { } notes)
            {
                w.Append("Notes: ").AppendLine(notes);
            }
            w.AppendLine();
        }
        return w.ToString().TrimEnd() + "\n";
    }

    public static (string Title, List<DeckSlide> Slides) Parse(string markdown)
    {
        var slides = new List<DeckSlide>();
        DeckSlide? slide = null;
        List<string>? notes = null;
        List<string>? chart = null;
        string? title = null;
        string? said = null;
        var subtitleNext = false;
        var column = 0;
        void Close()
        {
            if (slide is null)
            {
                return;
            }
            if (notes is { Count: > 0 })
            {
                slide.Notes = string.Join("\n", notes);
            }
            // A slide that does not say its layout shows what it holds: a table, steps, a quote; a heading alone is a section.
            if (said is null && slide.Layout == "bullets")
            {
                slide.Layout = slide.Table is { Columns.Count: > 0 } ? "table"
                    : slide.Items is { Count: > 0 } && slide.Bullets is null ? Agenda().IsMatch(slide.Title ?? "") ? "agenda" : "process"
                    : slide.Quote is not null && slide.Bullets is null ? "quote"
                    : slide.Chart is not null ? "chart"
                    : slide.Left is not null || slide.Right is not null ? (slide.LeftTitle ?? slide.RightTitle) is null ? "two" : "comparison"
                    : slide.Picture is not null && slide.Bullets is null ? "picture"
                    : slide.Bullets is null ? "section" : "bullets";
            }
            slides.Add(slide);
            slide = null;
            notes = null;
            said = null;
            column = 0;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (chart is not null)
            {
                if (line == "```")
                {
                    try
                    {
                        if (slide is not null)
                        {
                            slide.Chart = JsonSerializer.Deserialize<DeckChart>(string.Join("\n", chart), DeckModel.Json);
                        }
                    }
                    catch (JsonException)
                    {
                        // A chart that does not read is left out; its slide says its words.
                    }
                    chart = null;
                }
                else
                {
                    chart.Add(raw);
                }
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal) || line == "#")
            {
                Close();
                var heading = line.TrimStart('#').Trim();
                title ??= heading;
                slide = new DeckSlide { Layout = "title", Title = heading.Length > 0 ? heading : null };
                said = "title";
                subtitleNext = true;
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal) || line == "##")
            {
                Close();
                var heading = line[2..].Trim();
                if (Hint().Match(heading) is { Success: true } m && DeckModel.Layouts.Contains(m.Groups[2].Value))
                {
                    said = m.Groups[2].Value;
                    heading = m.Groups[1].Value.Trim();
                }
                slide = new DeckSlide { Layout = said ?? "bullets", Title = heading.Length > 0 ? heading : null };
                subtitleNext = false;
                continue;
            }
            if (line.Length == 0)
            {
                subtitleNext = false;
                if (notes is not null)
                {
                    notes.Add("");
                }
                continue;
            }
            if (slide is null)
            {
                if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                {
                    title = line[6..].Trim();
                }
                continue;
            }
            if (notes is not null)
            {
                notes.Add(raw.TrimEnd());
                continue;
            }
            if (line.StartsWith("notes:", StringComparison.OrdinalIgnoreCase))
            {
                notes = line[6..].Trim() is { Length: > 0 } first ? [first] : [];
                continue;
            }
            if (Origin().Match(line) is { Success: true } origin)
            {
                slide.Origin = new DeckOrigin { Slide = int.Parse(origin.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Read = origin.Groups[2].Success ? origin.Groups[2].Value : null };
                continue;
            }
            if (line == "```chart")
            {
                chart = [];
                continue;
            }
            if (slide.Layout == "title" && subtitleNext)
            {
                slide.Subtitle = slide.Subtitle is null ? line : slide.Subtitle + " " + line;
                continue;
            }
            if (line.StartsWith("subtitle:", StringComparison.OrdinalIgnoreCase))
            {
                slide.Subtitle = line[9..].Trim();
                continue;
            }
            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                column++;
                var heading = line.TrimStart('#').Trim() is { Length: > 0 } h ? h : null;
                if (column == 1)
                {
                    slide.LeftTitle = heading;
                    slide.Left ??= [];
                }
                else
                {
                    slide.RightTitle = heading;
                    slide.Right ??= [];
                }
                continue;
            }
            if (line.StartsWith('|'))
            {
                var cells = Cells(line);
                if (cells.All(c => Rule().IsMatch(c)))
                {
                    continue;
                }
                slide.Table ??= new DeckTable();
                if (slide.Table.Columns.Count == 0)
                {
                    slide.Table.Columns = cells;
                }
                else
                {
                    slide.Table.Rows.Add(cells);
                }
                continue;
            }
            if (line.StartsWith('>'))
            {
                slide.Quote = (slide.Quote is null ? "" : slide.Quote + " ") + line.TrimStart('>').Trim();
                continue;
            }
            if ((line.StartsWith("— ", StringComparison.Ordinal) || line.StartsWith("-- ", StringComparison.Ordinal)) && slide.Quote is not null)
            {
                slide.By = line.TrimStart('—', '-').Trim();
                continue;
            }
            if (Picture().Match(line) is { Success: true } picture)
            {
                slide.Picture = new DeckPicture
                {
                    Prompt = picture.Groups[1].Value,
                    FileId = picture.Groups[2].Value.StartsWith("file:", StringComparison.Ordinal) && Guid.TryParse(picture.Groups[2].Value[5..], out var id) ? id : null,
                };
                continue;
            }
            if (Numbered().Match(line) is { Success: true } step)
            {
                (slide.Items ??= []).Add(Item(step.Groups[1].Value));
                continue;
            }
            var point = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ? line[2..].Trim() : line;
            switch (column)
            {
                case 1:
                    slide.Left!.Add(point);
                    break;
                case 2:
                    slide.Right!.Add(point);
                    break;
                default:
                    (slide.Bullets ??= []).Add(point);
                    break;
            }
        }
        Close();
        foreach (var s in slides)
        {
            if (s.Notes is { } n)
            {
                s.Notes = n.TrimEnd();
            }
            s.Left = s.Left is { Count: > 0 } ? s.Left : null;
            s.Right = s.Right is { Count: > 0 } ? s.Right : null;
        }
        return (title ?? slides.FirstOrDefault()?.Title ?? "A deck", DeckModel.Clean(slides));
    }

    /// <summary>An item: "[42%] **Title** — words" as written here, or "Title: words" and "Title" as people write it.</summary>
    private static DeckItem Item(string s)
    {
        string? value = null;
        if (Value().Match(s) is { Success: true } v)
        {
            value = v.Groups[1].Value;
            s = s[v.Length..].TrimStart();
        }
        if (Bold().Match(s) is { Success: true } b)
        {
            return new DeckItem { Value = value, Title = b.Groups[1].Value is { Length: > 0 } t ? t : null, Text = b.Groups[2].Success && b.Groups[2].Value.Trim() is { Length: > 0 } x ? x : null };
        }
        var at = s.IndexOf(':', StringComparison.Ordinal);
        return at > 0 && at < s.Length - 1
            ? new DeckItem { Value = value, Title = s[..at].Trim(), Text = s[(at + 1)..].Trim() }
            : new DeckItem { Value = value, Title = s.Trim() };
    }

    /// <summary>A table row's cells, a "\|" being a bar in a cell.</summary>
    private static List<string> Cells(string line)
    {
        var inner = line.Trim();
        inner = inner.StartsWith('|') ? inner[1..] : inner;
        inner = inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal) ? inner[..^1] : inner;
        return [.. Bar().Split(inner).Select(c => c.Replace("\\|", "|", StringComparison.Ordinal).Trim())];
    }

    [GeneratedRegex(@"^#{1,2}\s+\S", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(.*?)\s*\{([a-z]+)\}$")]
    private static partial Regex Hint();

    [GeneratedRegex(@"^\d+[.)]\s+(.+)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^\[([^\]]{1,24})\]")]
    private static partial Regex Value();

    [GeneratedRegex(@"^\*\*(.*?)\*\*(?:\s*—\s*(.*))?$")]
    private static partial Regex Bold();

    [GeneratedRegex(@"^!\[(.*?)\]\((.*?)\)$")]
    private static partial Regex Picture();

    [GeneratedRegex(@"^<!--\s*slide\s+(\d+)(?:\s+([0-9A-F]{8,64}))?\s*-->$")]
    private static partial Regex Origin();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex Bar();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"\b(agenda|outline|contents|today)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Agenda();
}
