# Decks

Slides from a few words, an outline or a document. The model writes them,
with charts from the company's data and pictures from the picture model, in
the company's look or a built-in design. Change them by asking or by hand,
present them, and download them as PowerPoint or PDF. A PowerPoint file of
your own opens here to be revised, and comes back as that file.

## Making a deck

**Workspace → Decks.** In **Make a deck about…**:

- **A few words**, for example: "Our third quarter for the board: sales by
  region from Shop, the two new customers, hiring plans." The model plans the
  talk and writes it, usually six to ten slides.
- **An outline** in Markdown: its slides are made exactly as written, with no
  model (see [An outline](#an-outline)). The page says when it reads your words
  as one.
- **From a document of yours** (a PDF, Word or PowerPoint file, Markdown or
  text): the model makes the deck from what it says, its main points and
  figures; your words, if any, say how.

Choose its **Look** (a company template, or a built-in design: Arena,
Midnight, Paper, Bold or Minimal) and press **Make it**. In a chat, ask for
slides: they are a [slides canvas](chat.md#canvas), and a deck here too.

Each slide has one of these layouts, chosen for what it shows:

- **Title**, **Section** and **Closing** slides;
- **Agenda**: the parts of the talk, numbered;
- **Points**, with a picture beside them or not; **Two columns**;
- **Comparison**: two columns, each under its heading;
- **Chart**: bars (up or across, or stacked), a line, areas, a pie, a doughnut
  or points (x, y), with the takeaway beside it;
- **Table**: a header and up to 12 rows;
- **Big numbers**: up to four figures, each with what it counts;
- **Cards**: up to four, each a title and a few words;
- **Process**: up to five steps, left to right;
- **Timeline**: up to six dates or phases on a line;
- **Picture**, with a caption; **Quote**, with who said it.

Every slide has speaker notes. A slide with more than six points is split
into slides after it (its title continued): no point is lost. The deck is
written as you: your default model, your credit and your safeguards.

- **Charts** never get made-up numbers. A chart's numbers come either from
  your words (or your document) or from a database you may ask (see
  [Ask your data](data.md)). For a database, the chart keeps its question
  ("orders per month this year"), and **Ask the numbers again** brings
  today's numbers.
- **Pictures**: at most three a deck, unless you ask for more. They are
  made one at a time while the deck is open, as you, with the picture model
  you may use. A picture is made once, and the slide keeps it. Without a
  picture model, a slide shows what its picture would be.

## An outline

`# The title` makes the title slide; the lines under it are its subtitle. Each
`## A heading` is a slide. Under it:

| Write | For |
|---|---|
| `- a point` | points |
| `1. **Ask** — what people need` (or `1. Ask: what people need`) | an agenda's, process's, timeline's or cards' items |
| `1. [42%] **Growth** — since last year` | big numbers |
| `\| Office \| People \|` lines, the first the header | a table |
| `### Before` then points, `### After` then points | a comparison |
| `> Less is more` then `— Mies` | a quote |
| `![a visual description](file:)` | a picture the picture model makes |
| a ` ```chart ` block holding `{"type": "pie", "categories": [...], "series": [{"name": "...", "values": [...]}]}` | a chart |
| `Subtitle: …` | a section's subtitle, a picture's caption |
| `Notes: …` (to the next heading) | what to say |

A slide's layout is read from what it holds (a table, steps, a quote), or
said: `## Our offices {table}`. Steps under a heading that says agenda,
outline or contents are an agenda; others a process. A heading alone is a
section.

## Changing it

- **By asking**, below the slide, for example: "a slide on costs after the
  third", "make this slide shorter", "turn these points into a timeline".
  "This slide" is the one you are looking at. What you did not ask to change
  stays as it was, its pictures included.
- **By hand**: **Change by hand** opens the slide's form: the layout, the
  words, the items (one a line, their parts between bars: `42% | Growth`), the
  table (a row a line, cells between bars), the picture (describe a new one,
  or **Use a picture of yours**), the chart (its database and what it counts,
  and how it is drawn) and the speaker notes. The buttons beside it move the
  slide up or down, copy it, add a slide after it, or take it out.
- **Footer, numbers and transition** (in **⋯**): a footer and slide numbers
  (3 / 12) on every slide but the first and the closing one, and a fade, push
  or wipe from one slide to the next in PowerPoint.
- **Versions**: every change, asked or by hand, is a version. **Versions**
  lists them, and **Put back** makes an old one the newest.

## Revising a PowerPoint file

**Open a PowerPoint file** (a .pptx of at most 30 MB) reads its slides: their
titles, words, tables, charts (with their numbers), first picture and speaker
notes. Change them here as any deck, by asking or by hand. **PowerPoint**
then gives back that file:

- the slides you did not change stay exactly as they are in it, with their
  pictures, animations and look;
- the slides you changed or added are made on the file's own layouts, so they
  take its fonts and colours;
- the slides you took out are gone, and the order is the deck's.

Choosing a company template or a built-in design under **Look** lets go of
the file: from then on the deck is made in that look.

## Presenting and downloading

- **Present** fills the screen with one slide at a time. The next slide
  comes on a click, →, Space or Page Down. ← or Page Up goes back. Escape
  ends it.
- **PowerPoint** downloads a .pptx file:
  - each slide is on its template's layout of that kind, with its words in
    the layout's placeholders, so they take the template's fonts and colours;
  - tables, the agenda's circles, the process's chevrons, the timeline, cards
    and big numbers are PowerPoint's own tables and shapes, to edit there;
  - charts are PowerPoint's own, with their numbers in them;
  - words are fitted to their boxes (made smaller when there are many, never
    below half), and words on a colour are in whichever of the look's dark or
    light reads better on it;
  - web addresses are links; section slides start PowerPoint sections;
  - pictures are fitted to the slide;
  - speaker notes are kept when the template has a notes master.
- **PDF** prints the slides as the browser shows them, one slide a page:
  choose **Save as PDF** in the print dialog.

## The company's look (admins)

People who may change the branding (`settings.branding`) see
**Templates** on the Decks page.

- **Upload a template**: a .pptx or .potx file of at most 30 MB. Its slide
  masters, layouts, theme, fonts and logo carry into every deck made in it.
  Its own slides are left out. It is read when uploaded, so a file that
  cannot carry a deck is refused then.
- The first template becomes the default for new decks. **Make it the
  default** changes which template that is. A person can still choose
  another template, or a built-in design, under **Look**.
- The browser draws slides with the template's colours and fonts. The
  PowerPoint file carries all of the template: its backgrounds, logo and
  layouts.

Making decks needs the `feature.decks` permission. Everyone holds it unless
an admin denies it. Deck pictures are your files. Storage lists them as
**On a deck**, and its clean-ups leave them alone. A PowerPoint file opened
to revise is kept with its deck, and goes with it.
