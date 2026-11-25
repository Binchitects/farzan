using System.Text.RegularExpressions;

namespace Llm.Api.Decks;

/// <summary>
/// A deck from an outline in Markdown, as written, with no model: <c># Title</c> (and the line after it, its subtitle)
/// is the title slide; each <c>## Heading</c> a slide: its <c>- points</c>, a table of <c>| cells |</c>, a
/// <c>&gt; quote</c> (and <c>— who</c>), numbered steps (an agenda when the heading says so, else a process), a heading
/// alone a section; <c>Notes:</c> starts what to say.
/// </summary>
public static partial class DeckOutline
{
    /// <summary>Whether words look like an outline (a heading and something under it), not a request in prose.</summary>
    public static bool Looks(string text) => Heading().IsMatch(text) && text.Split('\n').Count(l => l.TrimStart().StartsWith('#')) >= 2;

    public static (string Title, List<DeckSlide> Slides) Parse(string markdown)
    {
        var slides = new List<DeckSlide>();
        DeckSlide? slide = null;
        List<string>? notes = null;
        string? title = null;
        var subtitleNext = false;
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
            // What a heading holds says its layout: a table, a quote, steps; a heading with nothing under it is a section.
            if (slide.Layout == "bullets" && slide.Table is { Columns.Count: > 0 })
            {
                slide.Layout = "table";
            }
            else if (slide.Layout == "bullets" && slide.Items is { Count: > 0 } && slide.Bullets is null)
            {
                slide.Layout = Agenda().IsMatch(slide.Title ?? "") ? "agenda" : "process";
            }
            else if (slide.Layout == "bullets" && slide.Quote is not null && slide.Bullets is null)
            {
                slide.Layout = "quote";
            }
            else if (slide.Layout == "bullets" && slide.Bullets is null && slide.Table is null && slide.Items is null && slide.Quote is null)
            {
                slide.Layout = "section";
            }
            slides.Add(slide);
            slide = null;
            notes = null;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                subtitleNext = false;
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Close();
                title = line[2..].Trim();
                slide = new DeckSlide { Layout = "title", Title = title };
                subtitleNext = true;
                continue;
            }
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                Close();
                slide = new DeckSlide { Layout = "bullets", Title = line.TrimStart('#').Trim() };
                continue;
            }
            if (slide is null)
            {
                continue;
            }
            if (notes is not null)
            {
                notes.Add(line);
                continue;
            }
            if (line.StartsWith("notes:", StringComparison.OrdinalIgnoreCase))
            {
                notes = line[6..].Trim() is { Length: > 0 } first ? [first] : [];
                continue;
            }
            if (slide.Layout == "title" && subtitleNext)
            {
                slide.Subtitle = slide.Subtitle is null ? line : slide.Subtitle + " " + line;
                continue;
            }
            if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
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
                slide.Quote = (slide.Quote is null ? "" : slide.Quote + " ") + line.TrimStart('>').Trim().Trim('"', '“', '”');
                continue;
            }
            if ((line.StartsWith("— ", StringComparison.Ordinal) || line.StartsWith("-- ", StringComparison.Ordinal)) && slide.Quote is not null)
            {
                slide.By = line.TrimStart('—', '-').Trim();
                continue;
            }
            if (Numbered().Match(line) is { Success: true } step)
            {
                var (head, text) = Split(step.Groups[1].Value);
                (slide.Items ??= []).Add(new DeckItem { Title = head, Text = text });
                continue;
            }
            var point = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ? line[2..].Trim() : line;
            (slide.Bullets ??= []).Add(point);
        }
        Close();
        return (title ?? slides.FirstOrDefault()?.Title ?? "A deck", DeckModel.Clean(slides));
    }

    /// <summary>"Plan: what we do" is a heading and its words; a line with no colon is a heading alone.</summary>
    private static (string Head, string? Text) Split(string s)
    {
        var at = s.IndexOf(':', StringComparison.Ordinal);
        return at > 0 && at < s.Length - 1 ? (s[..at].Trim(), s[(at + 1)..].Trim()) : (s.Trim(), null);
    }

    [GeneratedRegex(@"^#{1,2}\s+\S", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\d+[.)]\s+(.+)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"\b(agenda|outline|contents|today)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Agenda();
}
