// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Globalization;
using System.Text;
using Llm.Api.Chat;

namespace Llm.Api.Decks;

public static partial class DeckPptx
{
    /// <summary>One slide's XML and its relationships: shapes placed on the slide's size, words in the layout's placeholders.</summary>
    private sealed partial class SlideWriter(Package package, int number, int total, long width, long height, DeckLook look)
    {
        private readonly StringBuilder _shapes = new();
        private int _shape = 1;
        public List<(string Id, string Type, string Target)> Relations { get; } = [];

        public string Relate(string type, string target)
        {
            var id = $"rId{Relations.Count + 1}";
            Relations.Add((id, type, target));
            return id;
        }

        /// <summary>The words that read on a theme colour: its dark text or its paper, whichever stands out more from it.</summary>
        private string On(string fill) => package.Contrast(fill);

        private long X(double f) => (long)(width * f);
        private long Y(double f) => (long)(height * f);

        private static string Xfrm(long x, long y, long cx, long cy, string prefix = "a") =>
            $"<{prefix}:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></{prefix}:xfrm>";

        /// <summary>A placeholder of the layout (title, ctrTitle, subTitle, body…) at a place of ours, with its lines.</summary>
        private void Placeholder(string ph, IEnumerable<string> lines, double x, double y, double w, double h, int size = 22)
        {
            var id = ++_shape;
            var all = lines.ToList();
            var paragraphs = string.Concat(all.Select(l => Linked(l, null)));
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Placeholder {id}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(X(x), Y(y), X(w), Y(h))}</p:spPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:txBody><a:bodyPr>{Autofit(all, X(w), Y(h), size)}</a:bodyPr><a:lstStyle/>{(paragraphs.Length > 0 ? paragraphs : Para("", null))}</p:txBody></p:sp>");
        }

        /// <summary>
        /// The words fitted to their box as PowerPoint would shrink them, written out so every reader shows them so:
        /// lines wrapped at the box's width (a letter about half the size wide), each line 1.2 sizes high and each
        /// paragraph 10 points apart; too tall, the size and the spacing are cut together, never below half.
        /// </summary>
        internal static string Autofit(IReadOnlyList<string> lines, long boxWidth, long boxHeight, int size)
        {
            var scale = FitScale(lines, boxWidth, boxHeight, size);
            return scale >= 1 ? "<a:normAutofit/>" : $"<a:normAutofit fontScale=\"{(int)(scale * 100000)}\" lnSpcReduction=\"{(scale < 0.8 ? 20000 : 10000)}\"/>";
        }

        /// <summary>A paragraph whose web addresses are links (opened in the browser from the slide).</summary>
        private string Linked(string text, string? runProperties, string? alignment = null)
        {
            var links = Url().Matches(text);
            if (links.Count == 0)
            {
                return Para(text, runProperties, alignment);
            }
            var rPr = runProperties ?? "<a:rPr lang=\"en-US\" dirty=\"0\"/>";
            var runs = new StringBuilder();
            var at = 0;
            foreach (System.Text.RegularExpressions.Match m in links)
            {
                if (m.Index > at)
                {
                    runs.Append(CultureInfo.InvariantCulture, $"<a:r>{rPr}<a:t>{CanvasDocx.Esc(text[at..m.Index])}</a:t></a:r>");
                }
                var rel = Relate("hyperlink", m.Value);
                // The link last in the run's properties, as the schema orders them.
                var click = $"<a:hlinkClick r:id=\"{rel}\"/>";
                var linked = rPr.EndsWith("</a:rPr>", StringComparison.Ordinal)
                    ? rPr[..^"</a:rPr>".Length] + click + "</a:rPr>"
                    : rPr[..^"/>".Length] + ">" + click + "</a:rPr>";
                runs.Append(CultureInfo.InvariantCulture, $"<a:r>{linked}<a:t>{CanvasDocx.Esc(m.Value)}</a:t></a:r>");
                at = m.Index + m.Length;
            }
            if (at < text.Length)
            {
                runs.Append(CultureInfo.InvariantCulture, $"<a:r>{rPr}<a:t>{CanvasDocx.Esc(text[at..])}</a:t></a:r>");
            }
            var pPr = alignment is not null ? $"<a:pPr algn=\"{alignment}\"/>" : "";
            return $"<a:p>{pPr}{runs}</a:p>";
        }

        [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^\s<>""]+[^\s<>"".,;:!?)]")]
        private static partial System.Text.RegularExpressions.Regex Url();

        private static string RunProps(int size, bool bold = false, bool italic = false, string color = "tx1", bool heading = false) =>
            $"<a:rPr lang=\"en-US\" sz=\"{size * 100}\"{(italic ? " i=\"1\"" : "")}{(bold ? " b=\"1\"" : "")} dirty=\"0\"><a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"{(heading ? "+mj-lt" : "+mn-lt")}\"/><a:cs typeface=\"{(heading ? "+mj-cs" : "+mn-cs")}\"/></a:rPr>";

        /// <summary>
        /// A shape of PowerPoint's own (rect, roundRect, ellipse, chevron, homePlate…) filled with a theme colour, its
        /// paragraphs inside (each its size, boldness and colour), fitted; editable in PowerPoint as any shape is.
        /// </summary>
        private void Shape(string geometry, double x, double y, double w, double h, string? fill, string? line,
            List<(string Text, int Size, bool Bold, string Color)> paragraphs, string align = "ctr", string anchor = "ctr", bool heading = false)
        {
            var id = ++_shape;
            var fillXml = fill is null ? "<a:noFill/>" : fill.StartsWith('<') ? fill : $"<a:solidFill><a:schemeClr val=\"{fill}\"/></a:solidFill>";
            var lineXml = line is null ? "<a:ln><a:noFill/></a:ln>" : $"<a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"{line}\"/></a:solidFill></a:ln>";
            var body = string.Concat(paragraphs.Select((p, i) => Linked(p.Text, RunProps(p.Size, p.Bold, color: p.Color, heading: heading && i == 0), align)));
            var biggest = paragraphs.Count > 0 ? paragraphs.Max(p => p.Size) : 18;
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(X(x), Y(y), X(w), Y(h))}<a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom>{fillXml}{lineXml}</p:spPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"91440\" rIns=\"91440\" anchor=\"{anchor}\">{Autofit([.. paragraphs.Select(p => p.Text)], X(w) - 182880, Y(h) - 91440, biggest)}</a:bodyPr><a:lstStyle/>{(body.Length > 0 ? body : Para("", null))}</p:txBody></p:sp>");
        }

        /// <summary>A table of PowerPoint's own, in its built-in style (a header row in the accent colour, bands): editable there.</summary>
        private void Table(DeckTable table, double x, double y, double w, double h)
        {
            var id = ++_shape;
            var cols = table.Columns.Count;
            long colWidth = X(w) / cols;
            var rowCount = table.Rows.Count + 1;
            long rowHeight = Math.Min(Y(h) / rowCount, 640080);
            var size = rowCount > 8 || cols > 5 ? 12 : rowCount > 5 ? 14 : 16;
            // Each cell's fill said, not left to the table style (which assumes dark words on light paper): the header in
            // the accent with the words that read on it, the rows banded in the paper and its tint.
            string Cell(string text, string fill, string color, bool bold) =>
                $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>{Linked(text, RunProps(size, bold: bold, color: color))}</a:txBody><a:tcPr anchor=\"ctr\"><a:solidFill><a:schemeClr val=\"{fill}\"/></a:solidFill></a:tcPr></a:tc>";
            var rows = new StringBuilder();
            rows.Append(CultureInfo.InvariantCulture, $"<a:tr h=\"{rowHeight}\">{string.Concat(table.Columns.Select(c => Cell(c, "accent1", On("accent1"), true)))}</a:tr>");
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var band = i % 2 == 0 ? "bg2" : "bg1";
                rows.Append(CultureInfo.InvariantCulture, $"<a:tr h=\"{rowHeight}\">{string.Concat(table.Rows[i].Select(c => Cell(c, band, "tx1", false)))}</a:tr>");
            }
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>")
                .Append(Xfrm(X(x), Y(y), X(w), rowHeight * rowCount, "p"))
                .Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}</a:tableStyleId></a:tblPr><a:tblGrid>")
                .Append(string.Concat(Enumerable.Repeat($"<a:gridCol w=\"{colWidth}\"/>", cols)))
                .Append("</a:tblGrid>").Append(rows).Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
        }

        /// <summary>A text box of our own: a quote, a caption, a takeaway.</summary>
        private void TextBox(IEnumerable<string> lines, double x, double y, double w, double h, int size, bool italic = false, bool bold = false, string align = "l",
            string color = "tx1", string anchor = "t")
        {
            var id = ++_shape;
            var rPr = $"<a:rPr lang=\"en-US\" sz=\"{size * 100}\"{(italic ? " i=\"1\"" : "")}{(bold ? " b=\"1\"" : "")} dirty=\"0\"><a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:cs typeface=\"+mn-cs\"/></a:rPr>";
            var all = lines.ToList();
            var paragraphs = string.Concat(all.Select(l => Linked(l, rPr, align)));
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(X(x), Y(y), X(w), Y(h))}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:txBody><a:bodyPr wrap=\"square\" anchor=\"{anchor}\">{Autofit(all, X(w), Y(h), size)}</a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>");
        }

        /// <summary>A picture fitted inside the box (its shape kept), centred.</summary>
        private void Picture(DeckImage image, double x, double y, double w, double h)
        {
            var path = package.AddImage(image);
            var rel = Relate("image", "../media/" + Path.GetFileName(path));
            var (pw, ph) = Size(image.Data) ?? (4, 3);
            double boxW = X(w), boxH = Y(h);
            var scale = Math.Min(boxW / pw, boxH / ph);
            long cx = (long)(pw * scale), cy = (long)(ph * scale);
            long ox = X(x) + (long)((boxW - cx) / 2), oy = Y(y) + (long)((boxH - cy) / 2);
            var id = ++_shape;
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
                .Append(CultureInfo.InvariantCulture, $"<p:blipFill><a:blip r:embed=\"{rel}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>")
                .Append(CultureInfo.InvariantCulture, $"<p:spPr>{Xfrm(ox, oy, cx, cy)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        }

        /// <summary>A chart of PowerPoint's own, its numbers in it.</summary>
        private void Chart(DeckChart chart, double x, double y, double w, double h)
        {
            var path = package.AddChart(ChartXml(chart));
            var rel = Relate("chart", "../charts/" + Path.GetFileName(path));
            var id = ++_shape;
            _shapes.Append(CultureInfo.InvariantCulture, $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Chart {id}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>")
                .Append(Xfrm(X(x), Y(y), X(w), Y(h), "p"))
                .Append(CultureInfo.InvariantCulture, $"<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"{rel}\"/></a:graphicData></a:graphic></p:graphicFrame>");
        }

        private const double M = 0.06;
        private const double Wide = 1 - 2 * M;

        public string Slide(DeckSlide s, IReadOnlyDictionary<Guid, DeckImage> images)
        {
            DeckImage? picture = s.Picture?.FileId is { } file && images.TryGetValue(file, out var found) ? found : null;
            var title = s.Title ?? "";
            switch (s.Layout)
            {
                case "title":
                    Placeholder("<p:ph type=\"ctrTitle\"/>", [title], M, 0.30, Wide, 0.24);
                    Placeholder("<p:ph type=\"subTitle\" idx=\"1\"/>", [s.Subtitle ?? ""], M, 0.56, Wide, 0.16);
                    break;
                case "section":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.34, Wide, 0.2);
                    Placeholder("<p:ph type=\"body\" idx=\"1\"/>", [s.Subtitle ?? ""], M, 0.56, Wide, 0.14);
                    break;
                case "two":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    Placeholder("<p:ph sz=\"half\" idx=\"1\"/>", s.Left ?? [], M, 0.25, Wide / 2 - 0.02, 0.68);
                    Placeholder("<p:ph sz=\"half\" idx=\"2\"/>", s.Right ?? [], M + Wide / 2 + 0.02, 0.25, Wide / 2 - 0.02, 0.68);
                    break;
                case "chart":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    var takeaway = s.Bullets ?? [];
                    var chartWidth = takeaway.Count > 0 ? Wide * 0.66 : Wide;
                    if (s.Chart is { Categories.Count: > 0, Series.Count: > 0 } chart)
                    {
                        Chart(chart, M, 0.24, chartWidth, 0.7);
                    }
                    else
                    {
                        TextBox([s.Chart?.Note ?? "No numbers yet."], M, 0.4, chartWidth, 0.2, 18, italic: true, color: "tx2", align: "ctr", anchor: "ctr");
                    }
                    if (takeaway.Count > 0)
                    {
                        TextBox(takeaway, M + chartWidth + 0.02, 0.28, Wide - chartWidth - 0.02, 0.6, 18, anchor: "ctr");
                    }
                    break;
                case "picture":
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    if (picture is not null)
                    {
                        Picture(picture, M, 0.24, Wide, s.Subtitle is null ? 0.7 : 0.6);
                    }
                    if (s.Subtitle is { } caption)
                    {
                        TextBox([caption], M, 0.86, Wide, 0.08, 14, color: "tx2", align: "ctr");
                    }
                    break;
                case "quote":
                    TextBox([$"“{s.Quote ?? title}”"], M + 0.04, 0.2, Wide - 0.08, 0.48, 32, italic: true, align: "ctr", anchor: "ctr");
                    if (s.By is { } by)
                    {
                        TextBox([$"— {by}"], M + 0.04, 0.7, Wide - 0.08, 0.1, 18, color: "tx2", align: "ctr");
                    }
                    break;
                case "closing":
                    Placeholder("<p:ph type=\"ctrTitle\"/>", [title.Length > 0 ? title : "Thank you"], M, 0.32, Wide, 0.22);
                    Placeholder("<p:ph type=\"subTitle\" idx=\"1\"/>", [.. new[] { s.Subtitle ?? "" }.Concat(s.Bullets ?? [])], M, 0.56, Wide, 0.3);
                    break;
                case "agenda":
                    Heading(title);
                    Agenda(s.Items ?? []);
                    break;
                case "comparison":
                    Heading(title);
                    Column(s.LeftTitle, s.Left ?? [], M, "accent1");
                    Column(s.RightTitle, s.Right ?? [], M + Wide / 2 + 0.015, "accent2");
                    break;
                case "table":
                    Heading(title);
                    if (s.Table is { } table)
                    {
                        Table(table, M, 0.25, Wide, 0.68);
                    }
                    break;
                case "stats":
                    Heading(title);
                    Stats(s.Items ?? []);
                    break;
                case "cards":
                    Heading(title);
                    Cards(s.Items ?? []);
                    break;
                case "process":
                    Heading(title);
                    Process(s.Items ?? []);
                    break;
                case "timeline":
                    Heading(title);
                    Timeline(s.Items ?? []);
                    break;
                default:
                    Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16);
                    var textWidth = picture is null ? Wide : Wide * 0.56;
                    Placeholder("<p:ph idx=\"1\"/>", s.Bullets ?? [], M, 0.25, textWidth, 0.68);
                    if (picture is not null)
                    {
                        Picture(picture, M + textWidth + 0.03, 0.25, Wide - textWidth - 0.03, 0.68);
                    }
                    break;
            }
            // The footer and the slide's number, on every slide but the title and the closing slide.
            if (s.Layout is not ("title" or "closing"))
            {
                if (look.Footer is { } footer)
                {
                    TextBox([footer], M, 0.935, Wide * 0.7, 0.05, 11, color: "tx2", anchor: "ctr");
                }
                if (look.Numbers)
                {
                    TextBox([$"{number} / {total}"], M + Wide * 0.75, 0.935, Wide * 0.25, 0.05, 11, color: "tx2", align: "r", anchor: "ctr");
                }
            }
            var transition = look.Transition switch
            {
                "fade" => "<p:transition spd=\"med\"><p:fade/></p:transition>",
                "push" => "<p:transition spd=\"med\"><p:push dir=\"u\"/></p:transition>",
                "wipe" => "<p:transition spd=\"med\"><p:wipe dir=\"r\"/></p:transition>",
                _ => "",
            };
            return $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld name="Slide {number}"><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{_shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>{transition}</p:sld>
                """;
        }

        private void Heading(string title) => Placeholder("<p:ph type=\"title\"/>", [title], M, 0.05, Wide, 0.16, 36);

        /// <summary>An agenda: each item numbered in an accent circle, its words beside it, in one or two columns.</summary>
        private void Agenda(List<DeckItem> items)
        {
            var twoColumns = items.Count > 4;
            var perColumn = twoColumns ? (items.Count + 1) / 2 : items.Count;
            var rowH = Math.Min(0.15, 0.66 / Math.Max(perColumn, 1));
            for (var i = 0; i < items.Count; i++)
            {
                var col = twoColumns && i >= perColumn ? 1 : 0;
                var row = col == 1 ? i - perColumn : i;
                var x = M + col * (Wide / 2 + 0.01);
                var y = 0.26 + row * rowH;
                var dot = rowH * 0.62 * height / width;
                Shape("ellipse", x, y + rowH * 0.12, dot, rowH * 0.62, "accent1", null, [((i + 1).ToString(CultureInfo.InvariantCulture), 18, true, On("accent1"))]);
                var words = new List<(string, int, bool, string)>();
                if (items[i].Title is { } t)
                {
                    words.Add((t, 22, true, "tx1"));
                }
                if (items[i].Text is { } d)
                {
                    words.Add((d, 14, false, "tx2"));
                }
                Shape("rect", x + dot + 0.015, y, (twoColumns ? Wide / 2 - 0.01 : Wide) - dot - 0.015, rowH * 0.86, null, null, words, "l", "ctr");
            }
        }

        /// <summary>A comparison's side: its heading on an accent band, its points under it.</summary>
        private void Column(string? heading, List<string> points, double x, string accent)
        {
            var w = Wide / 2 - 0.015;
            Shape("rect", x, 0.25, w, 0.09, accent, null, [(heading ?? "", 20, true, On(accent))]);
            Shape("rect", x, 0.34, w, 0.58, "bg2", null, [.. points.Select(p => ("• " + p, 18, false, "tx1"))], "l", "t");
        }

        /// <summary>Big numbers: each figure large in the accent colour, its label and words under it, side by side.</summary>
        private void Stats(List<DeckItem> items)
        {
            var n = Math.Max(items.Count, 1);
            var gap = 0.02;
            var w = (Wide - gap * (n - 1)) / n;
            for (var i = 0; i < items.Count; i++)
            {
                var x = M + i * (w + gap);
                Shape("rect", x, 0.3, w, 0.3, null, null, [(items[i].Value ?? "", n > 3 ? 54 : 66, true, $"accent{i % 6 + 1}")], heading: true);
                var words = new List<(string, int, bool, string)>();
                if (items[i].Title is { } t)
                {
                    words.Add((t, 20, true, "tx1"));
                }
                if (items[i].Text is { } d)
                {
                    words.Add((d, 14, false, "tx2"));
                }
                Shape("rect", x, 0.6, w, 0.3, null, null, words, "ctr", "t");
            }
        }

        /// <summary>Cards: a rounded box each, an accent bar on top, its title and words.</summary>
        private void Cards(List<DeckItem> items)
        {
            var n = Math.Max(items.Count, 1);
            var gap = 0.02;
            var w = (Wide - gap * (n - 1)) / n;
            for (var i = 0; i < items.Count; i++)
            {
                var x = M + i * (w + gap);
                Shape("rect", x, 0.27, w, 0.015, $"accent{i % 6 + 1}", null, []);
                var words = new List<(string, int, bool, string)>();
                if (items[i].Title is { } t)
                {
                    words.Add((t, 20, true, "tx1"));
                }
                if (items[i].Text is { } d)
                {
                    words.Add((d, 15, false, "tx2"));
                }
                Shape("roundRect", x, 0.285, w, 0.62, "bg2", null, words, "l", "t");
            }
        }

        /// <summary>A process: its steps as chevrons in a row, left to right, each one's words under it.</summary>
        private void Process(List<DeckItem> items)
        {
            var n = Math.Max(items.Count, 1);
            var w = Wide / n;
            for (var i = 0; i < items.Count; i++)
            {
                var x = M + i * w;
                Shape(i == 0 ? "homePlate" : "chevron", x, 0.3, w * 1.02, 0.16, $"accent{i % 6 + 1}", null, [(items[i].Title ?? $"Step {i + 1}", 18, true, On($"accent{i % 6 + 1}"))]);
                if (items[i].Text is { } d)
                {
                    Shape("rect", x + 0.01, 0.5, w - 0.02, 0.4, null, null, [(d, 15, false, "tx1")], "ctr", "t");
                }
            }
        }

        /// <summary>A timeline: a line across, a dot for each item, its date above and its words below.</summary>
        private void Timeline(List<DeckItem> items)
        {
            var n = Math.Max(items.Count, 1);
            var w = Wide / n;
            Shape("rect", M, 0.5, Wide, 0.008, "tx2", null, []);
            for (var i = 0; i < items.Count; i++)
            {
                var centre = M + w * (i + 0.5);
                var dot = 0.035;
                Shape("ellipse", centre - dot * height / width / 2, 0.5 + 0.004 - dot / 2, dot * height / width, dot, $"accent{i % 6 + 1}", null, []);
                Shape("rect", centre - w / 2, 0.3, w, 0.17, null, null, [(items[i].Title ?? "", 18, true, $"accent{i % 6 + 1}")], "ctr", "b");
                if (items[i].Text is { } d)
                {
                    Shape("rect", centre - w / 2 + 0.005, 0.55, w - 0.01, 0.35, null, null, [(d, 14, false, "tx1")], "ctr", "t");
                }
            }
        }
    }

    /// <summary>How much smaller the words must be to fit their box (1: as they are; at least 0.5), as <c>Autofit</c> writes it.</summary>
    public static double FitScale(IReadOnlyList<string> lines, long boxWidth, long boxHeight, int size)
    {
        const double EmuPerPoint = 12700;
        double width = boxWidth / EmuPerPoint, height = boxHeight / EmuPerPoint;
        double Needed(double pt)
        {
            var perLine = Math.Max(1, (int)(width / (pt * 0.5)));
            return lines.Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / (double)perLine)) * pt * 1.2 + 10);
        }
        if (lines.Count == 0 || Needed(size) <= height)
        {
            return 1;
        }
        var scale = 1.0;
        while (scale > 0.5 && Needed(size * scale) > height)
        {
            scale -= 0.05;
        }
        return Math.Round(Math.Max(scale, 0.5), 2);
    }

    /// <summary>A picture's width and height from its header (PNG, JPEG, GIF), or null.</summary>
    public static (double W, double H)? Size(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        }
        if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
        {
            return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length && b[i] == 0xFF)
            {
                var marker = b[i + 1];
                var length = (b[i + 2] << 8) | b[i + 3];
                // A start of frame (not DHT, JPG or DAC): its height then its width.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                }
                i += 2 + length;
            }
        }
        return null;
    }

    /// <summary>
    /// A chart of PowerPoint's own, its numbers as literals (no workbook), in the theme's accent colours: bars up or across,
    /// stacked, lines, areas, a pie or doughnut (one series, each slice its colour and share), or points (scatter: the
    /// categories are the x numbers). A legend for more than one series, or for a pie's slices.
    /// </summary>
    internal static string ChartXml(DeckChart chart)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Color(int i) => $"<a:schemeClr val=\"accent{i % 6 + 1}\"/>";
        var type = chart.Type;
        var round = type is "pie" or "doughnut";
        var count = chart.Categories.Count;
        string Strings()
        {
            var w = new StringBuilder($"<c:cat><c:strLit><c:ptCount val=\"{count}\"/>");
            for (var i = 0; i < count; i++)
            {
                w.Append(CultureInfo.InvariantCulture, $"<c:pt idx=\"{i}\"><c:v>{CanvasDocx.Esc(chart.Categories[i])}</c:v></c:pt>");
            }
            return w.Append("</c:strLit></c:cat>").ToString();
        }
        string Numbers(string tag, IReadOnlyList<double?> values)
        {
            var w = new StringBuilder($"<c:{tag}><c:numLit><c:formatCode>General</c:formatCode><c:ptCount val=\"{count}\"/>");
            for (var i = 0; i < values.Count && i < count; i++)
            {
                if (values[i] is { } v)
                {
                    w.Append(CultureInfo.InvariantCulture, $"<c:pt idx=\"{i}\"><c:v>{N(v)}</c:v></c:pt>");
                }
            }
            return w.Append(CultureInfo.InvariantCulture, $"</c:numLit></c:{tag}>").ToString();
        }
        // Scatter: the categories read as numbers (those that are not one are left out of every series).
        var xs = chart.Categories.Select(c => double.TryParse(c.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null).ToList();
        var series = new StringBuilder();
        for (var s = 0; s < chart.Series.Count; s++)
        {
            var ser = chart.Series[s];
            series.Append(CultureInfo.InvariantCulture, $"<c:ser><c:idx val=\"{s}\"/><c:order val=\"{s}\"/><c:tx><c:v>{CanvasDocx.Esc(ser.Name)}</c:v></c:tx>");
            switch (type)
            {
                case "line":
                    series.Append(CultureInfo.InvariantCulture, $"<c:spPr><a:ln w=\"28575\" cap=\"rnd\"><a:solidFill>{Color(s)}</a:solidFill><a:round/></a:ln></c:spPr><c:marker><c:symbol val=\"none\"/></c:marker>")
                        .Append(Strings()).Append(Numbers("val", ser.Values)).Append("<c:smooth val=\"0\"/>");
                    break;
                case "scatter":
                    series.Append(CultureInfo.InvariantCulture, $"<c:spPr><a:ln w=\"19050\"><a:noFill/></a:ln></c:spPr><c:marker><c:symbol val=\"circle\"/><c:size val=\"8\"/><c:spPr><a:solidFill>{Color(s)}</a:solidFill><a:ln><a:noFill/></a:ln></c:spPr></c:marker>")
                        .Append(Numbers("xVal", xs)).Append(Numbers("yVal", ser.Values)).Append("<c:smooth val=\"0\"/>");
                    break;
                case "pie" or "doughnut":
                    for (var i = 0; i < count; i++)
                    {
                        series.Append(CultureInfo.InvariantCulture, $"<c:dPt><c:idx val=\"{i}\"/><c:bubble3D val=\"0\"/><c:spPr><a:solidFill>{Color(i)}</a:solidFill><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill></a:ln></c:spPr></c:dPt>");
                    }
                    series.Append("<c:dLbls><c:numFmt formatCode=\"0%\" sourceLinked=\"0\"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr><c:showLegendKey val=\"0\"/><c:showVal val=\"0\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"1\"/><c:showBubbleSize val=\"0\"/><c:showLeaderLines val=\"1\"/></c:dLbls>")
                        .Append(Strings()).Append(Numbers("val", ser.Values));
                    break;
                case "area":
                    series.Append(CultureInfo.InvariantCulture, $"<c:spPr><a:solidFill><a:schemeClr val=\"accent{s % 6 + 1}\"><a:alpha val=\"70000\"/></a:schemeClr></a:solidFill></c:spPr>")
                        .Append(Strings()).Append(Numbers("val", ser.Values));
                    break;
                default:
                    series.Append(CultureInfo.InvariantCulture, $"<c:spPr><a:solidFill>{Color(s)}</a:solidFill></c:spPr><c:invertIfNegative val=\"0\"/>")
                        .Append(Strings()).Append(Numbers("val", ser.Values));
                    break;
            }
            series.Append("</c:ser>");
        }
        const string Axes = "<c:axId val=\"1001\"/><c:axId val=\"1002\"/>";
        var plot = type switch
        {
            "line" => $"<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>{series}<c:marker val=\"1\"/>{Axes}</c:lineChart>",
            "area" => $"<c:areaChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>{series}{Axes}</c:areaChart>",
            "scatter" => $"<c:scatterChart><c:scatterStyle val=\"lineMarker\"/><c:varyColors val=\"0\"/>{series}{Axes}</c:scatterChart>",
            "pie" => $"<c:pieChart><c:varyColors val=\"1\"/>{series}<c:firstSliceAng val=\"0\"/></c:pieChart>",
            "doughnut" => $"<c:doughnutChart><c:varyColors val=\"1\"/>{series}<c:firstSliceAng val=\"0\"/><c:holeSize val=\"55\"/></c:doughnutChart>",
            "stacked" => $"<c:barChart><c:barDir val=\"col\"/><c:grouping val=\"stacked\"/><c:varyColors val=\"0\"/>{series}<c:gapWidth val=\"80\"/><c:overlap val=\"100\"/>{Axes}</c:barChart>",
            "hbar" => $"<c:barChart><c:barDir val=\"bar\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>{series}<c:gapWidth val=\"80\"/>{Axes}</c:barChart>",
            _ => $"<c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>{series}<c:gapWidth val=\"80\"/>{Axes}</c:barChart>",
        };
        const string Gray = "<c:spPr><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"tx1\"><a:lumMod val=\"15000\"/><a:lumOff val=\"85000\"/></a:schemeClr></a:solidFill></a:ln></c:spPr>";
        // Bars across have their categories up the left and their values along the bottom.
        var (catPos, valPos) = type == "hbar" ? ("l", "b") : ("b", "l");
        var first = type == "scatter"
            ? $"<c:valAx><c:axId val=\"1001\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"b\"/><c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>{Gray}<c:crossAx val=\"1002\"/><c:crosses val=\"autoZero\"/><c:crossBetween val=\"midCat\"/></c:valAx>"
            : $"<c:catAx><c:axId val=\"1001\"/><c:scaling><c:orientation val=\"{(type == "hbar" ? "maxMin" : "minMax")}\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{catPos}\"/><c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>{Gray}<c:crossAx val=\"1002\"/><c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/></c:catAx>";
        var axes = round ? "" : first + $"<c:valAx><c:axId val=\"1002\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{valPos}\"/><c:majorGridlines>{Gray}</c:majorGridlines><c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/><c:spPr><a:ln><a:noFill/></a:ln></c:spPr><c:crossAx val=\"1001\"/><c:crosses val=\"{(type == "hbar" ? "max" : "autoZero")}\"/><c:crossBetween val=\"between\"/></c:valAx>";
        var legend = chart.Series.Count > 1 || round ? $"<c:legend><c:legendPos val=\"{(round ? "r" : "b")}\"/><c:overlay val=\"0\"/></c:legend>" : "";
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="{A}" xmlns:r="{R}"><c:roundedCorners val="0"/><c:chart><c:autoTitleDeleted val="1"/><c:plotArea><c:layout/>{plot}{axes}</c:plotArea>{legend}<c:plotVisOnly val="1"/><c:dispBlanksAs val="gap"/></c:chart><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz="1400"><a:solidFill><a:schemeClr val="tx1"><a:lumMod val="75000"/><a:lumOff val="25000"/></a:schemeClr></a:solidFill><a:latin typeface="+mn-lt"/></a:defRPr></a:pPr><a:endParaRPr lang="en-US"/></a:p></c:txPr></c:chartSpace>
            """;
    }
}
