// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Decks;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Decks;

/// <summary>
/// A slides canvas and its deck kept as one: the canvas holds the deck as Markdown (<see cref="DeckOutline"/>), and each
/// change to it, by the person or the model, is a version of the deck (under Decks too); a change made to the deck there is
/// written back into the canvas as the person's. What the model writes keeps only the numbers someone gave (the chat's
/// questions, or the deck before), as decks made by asking do; a chart that asks a database gets its numbers.
/// </summary>
public sealed class SlideCanvases(AppDbContext db, DeckMaker maker, Canvases canvases)
{
    /// <summary>
    /// The canvas's deck made again from its text: made the first time (in the default template), else a new version. The
    /// text is then the deck's as kept (a chart's numbers filled in, the made-up ones out), in place, without another version.
    /// </summary>
    public async Task<Deck?> SyncAsync(Canvas canvas, AppUser owner, bool byModel, CancellationToken ct)
    {
        if (canvas.Kind != "slides")
        {
            return null;
        }
        var (title, slides) = DeckOutline.Parse(canvas.Content);
        if (slides.Count == 0)
        {
            return null;
        }
        var deck = canvas.DeckId is { } id ? await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == owner.Id, ct) : null;
        if (byModel)
        {
            // The numbers the person gave: their words in this chat, and the deck's own before.
            var asked = string.Join("\n", await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == canvas.ConversationId && m.Role == "user").Select(m => m.Content).ToListAsync(ct));
            DeckMaker.DropMadeUp(slides, asked, deck is null ? null : DeckModel.Read(deck.Slides));
        }
        await maker.CheckFilesAsync(slides, owner, ct);
        await maker.FillChartsAsync(owner, slides, false, ct);
        title = title.Length > 200 ? title[..200] : title;
        var note = $"From the canvas \"{canvas.Title}\", version {canvas.Version}";
        if (deck is null)
        {
            var template = await db.DeckTemplates.Where(t => t.IsDefault).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
            deck = new Deck { OwnerId = owner.Id, Title = title, Slides = DeckModel.Write(slides), TemplateId = template };
            db.Decks.Add(deck);
            db.DeckVersions.Add(new DeckVersion { DeckId = deck.Id, Version = 1, Title = deck.Title, Slides = deck.Slides, Note = note });
            canvas.DeckId = deck.Id;
        }
        else if (deck.Slides != DeckModel.Write(slides) || deck.Title != title)
        {
            deck.Title = title;
            deck.Slides = DeckModel.Write(slides);
            deck.Version++;
            deck.UpdatedAt = DateTimeOffset.UtcNow;
            db.DeckVersions.Add(new DeckVersion { DeckId = deck.Id, Version = deck.Version, Title = deck.Title, Slides = deck.Slides, Note = note });
        }
        // The text as the deck keeps it (cleaned, filled), in the version just made.
        var kept = DeckOutline.Write(title, slides);
        if (kept != canvas.Content)
        {
            canvas.Content = kept;
            await db.CanvasVersions.Where(v => v.CanvasId == canvas.Id && v.Number == canvas.Version).ExecuteUpdateAsync(u => u.SetProperty(v => v.Content, kept), ct);
        }
        await db.SaveChangesAsync(ct);
        return deck;
    }

    /// <summary>A deck changed under Decks: its canvas (if it has one) gets its text as a version by the person.</summary>
    public async Task FromDeckAsync(Deck deck, IReadOnlyList<DeckSlide> slides, string why, CancellationToken ct)
    {
        if (await db.Canvases.SingleOrDefaultAsync(c => c.DeckId == deck.Id, ct) is not { } canvas)
        {
            return;
        }
        try
        {
            await canvases.ChangeAsync(canvas, DeckOutline.Write(deck.Title, slides), deck.Title, Canvases.Person, why, null, ct);
        }
        catch (CanvasConflictException)
        {
            // Changed in the chat at the same moment: the canvas keeps that change, and makes the deck again from it.
        }
    }
}
