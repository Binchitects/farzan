using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Insights;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Decks;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Decks;

/// <param name="About">What it is about, in the person's words; or an outline in Markdown (# and ## headings), made into slides as written.</param>
/// <param name="FileId">One of the person's files (a PDF, Word or PowerPoint document, text): the deck is made from what it says.</param>
/// <param name="CanvasId">One of the person's canvases: the deck is made from it.</param>
public sealed record NewDeck(string? About, Guid? TemplateId = null, DeckLook? Look = null, Guid? FileId = null, Guid? CanvasId = null);

/// <param name="Slide">The slide the person is looking at (from 0): "this slide".</param>
public sealed record DeckChange(string? Change, int? Slide = null);

/// <param name="TemplateId">Guid.Empty: the built-in look.</param>
/// <param name="Look">Its design (used without a template), footer, slide numbers and transition.</param>
public sealed record DeckEdit(string? Title = null, List<DeckSlide>? Slides = null, Guid? TemplateId = null, string? Note = null, DeckLook? Look = null);

public sealed record TemplateChange(string? Name = null, bool? IsDefault = null);

/// <summary>
/// Decks: made from a person's words by the model, changed by asking or by hand (every version kept), their charts' numbers
/// asked of the data, their pictures made one by one, downloaded as PowerPoint in the company's template. Each person's own.
/// </summary>
public static class DeckEndpoints
{
    public const int MostTemplateBytes = 30 * 1024 * 1024;

    public static void MapDecks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/decks").RequireFeature("decks").PersonOnly().RequirePermission(Core.Access.Permissions.Decks);
        g.MapGet("", ListAsync);
        g.MapGet("/config", ConfigAsync);
        g.MapPost("", MakeAsync);
        g.MapPost("/import", ImportAsync).DisableAntiforgery();
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", EditAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
        g.MapPost("/{id:guid}/ask", ChangeAsync);
        g.MapPost("/{id:guid}/refresh", RefreshAsync);
        g.MapPost("/{id:guid}/slides/{index:int}/picture", PictureAsync);
        g.MapGet("/{id:guid}/versions", VersionsAsync);
        g.MapPost("/{id:guid}/versions/{version:int}/restore", RestoreAsync);
        g.MapGet("/{id:guid}/pptx", PptxAsync);

        var admin = app.MapGroup("/api/admin/deck-templates").RequireFeature("decks").RequirePermissions(Core.Access.Permissions.SettingsRead, Core.Access.Permissions.SettingsBranding);
        admin.MapGet("", TemplatesAsync);
        admin.MapPost("", UploadAsync).DisableAntiforgery();
        admin.MapPatch("/{id:guid}", ChangeTemplateAsync);
        admin.MapDelete("/{id:guid}", RemoveTemplateAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Brief(Deck d) => new { d.Id, d.Title, d.TemplateId, d.Version, d.CreatedAt, d.UpdatedAt };

    private static object Full(Deck d, List<DeckSlide> slides, string? source = null) =>
        new { d.Id, d.Title, d.TemplateId, look = DeckModel.ReadLook(d.Look), source, d.Version, d.CreatedAt, d.UpdatedAt, slides };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var decks = await db.Decks.AsNoTracking().Where(d => d.OwnerId == me.Id).OrderByDescending(d => d.UpdatedAt).Take(DeckMaker.MostDecks).ToListAsync(ct);
        // The first slide of each, for its card.
        return Results.Ok(decks.Select(d => DeckModel.Read(d.Slides) is var s ? new { d.Id, d.Title, d.TemplateId, look = DeckModel.ReadLook(d.Look), d.Version, d.UpdatedAt, slides = s.Count, first = s.FirstOrDefault() } : null));
    }

    /// <summary>What a deck can have here: pictures (a picture model this person may use), the company's templates, the databases they may ask.</summary>
    private static async Task<IResult> ConfigAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ToolRegistry registry, AccessService access, ImageTool images,
        DataQuestions data, CancellationToken ct)
    {
        var me = await Me(p, users);
        var templates = await db.DeckTemplates.AsNoTracking().OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsDefault, t.Theme }).ToListAsync(ct);
        return Results.Ok(new
        {
            pictures = await PicturesAsync(me, registry, access, images, ct),
            templates = templates.Select(t => new { t.Id, t.Name, t.IsDefault, theme = JsonNode.Parse(t.Theme) }),
            designs = DeckModel.Designs.Select(name => new { name, theme = JsonNode.Parse(DeckPptx.DesignTheme(name)) }),
            layouts = DeckModel.Layouts,
            charts = DeckModel.Charts,
            transitions = DeckModel.Transitions,
            sources = (await data.SourcesForAsync(me, ct)).Select(s => s.Name),
        });
    }

    private static async Task<bool> PicturesAsync(AppUser me, ToolRegistry registry, AccessService access, ImageTool images, CancellationToken ct) =>
        await images.UnavailableAsync(ct) is null && await registry.MayUseAsync(await access.MembershipAsync(me, ct), images.Id, ct);

    private static async Task<IResult> MakeAsync(NewDeck body, ClaimsPrincipal p, UserManager<AppUser> users, DeckMaker maker, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (deck, slides, problem) = await maker.CreateAsync(me, body.About ?? "", body.TemplateId, ct, body.Look, body.FileId, body.CanvasId);
        return problem ?? Results.Created($"/api/decks/{deck!.Id}", Full(deck, slides));
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..(most - 1)] + "…" : s;

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.Decks.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is { } d
            ? Results.Ok(Full(d, DeckModel.Read(d.Slides), await db.DeckSources.AsNoTracking().Where(x => x.DeckId == d.Id).Select(x => x.FileName).SingleOrDefaultAsync(ct)))
            : Results.NotFound();
    }

    /// <summary>A new version of the deck: its slides, title or template changed.</summary>
    private static void Save(Deck d, AppDbContext db, string title, List<DeckSlide> slides, string note)
    {
        d.Title = title;
        d.Slides = DeckModel.Write(slides);
        d.Version++;
        d.UpdatedAt = DateTimeOffset.UtcNow;
        db.DeckVersions.Add(new DeckVersion { DeckId = d.Id, Version = d.Version, Title = d.Title, Slides = d.Slides, Note = Cut(note, 500) });
    }

    private static async Task<IResult> EditAsync(Guid id, DeckEdit body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, SlideCanvases canvas, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        if (body.TemplateId is { } template)
        {
            if (template != Guid.Empty && !await db.DeckTemplates.AnyAsync(t => t.Id == template, ct))
            {
                return AuthEndpoints.Problem(400, "template", "That template is not here.");
            }
            d.TemplateId = template == Guid.Empty ? null : template;
            d.UpdatedAt = DateTimeOffset.UtcNow;
            // A deck opened from a file and put in a look of ours is made in that look from now on: the file is let go.
            await db.DeckSources.Where(x => x.DeckId == d.Id).ExecuteDeleteAsync(ct);
        }
        if (body.Look is { } look)
        {
            d.Look = DeckModel.Write(DeckModel.Clean(look));
            d.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var title = body.Title?.Trim() is { Length: > 0 } t ? Cut(t, 200) : d.Title;
        if (body.Slides is { } sent)
        {
            if (sent.Count is 0 or > DeckModel.MostSlides)
            {
                return AuthEndpoints.Problem(400, "slides", $"A deck has 1 to {DeckModel.MostSlides} slides.");
            }
            var slides = DeckModel.Clean(sent);
            await maker.CheckFilesAsync(slides, me, ct);
            // A chart given a question by hand gets its numbers now.
            await maker.FillChartsAsync(me, slides, false, ct);
            Save(d, db, title, slides, body.Note ?? "Edited");
        }
        else if (title != d.Title)
        {
            Save(d, db, title, DeckModel.Read(d.Slides), body.Note ?? "Renamed");
        }
        await db.SaveChangesAsync(ct);
        await canvas.FromDeckAsync(d, DeckModel.Read(d.Slides), "Changed in Decks", ct);
        return Results.Ok(Full(d, DeckModel.Read(d.Slides)));
    }

    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        db.Decks.Remove(d);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Changed as the person asks: pictures kept where their prompt is the same, new charts' numbers asked of the data.</summary>
    private static async Task<IResult> ChangeAsync(Guid id, DeckChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, SlideCanvases canvas, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var change = body.Change?.Trim() ?? "";
        if (change.Length is 0 or > 4000)
        {
            return AuthEndpoints.Problem(400, "change", "Say what to change, up to 4,000 characters.");
        }
        var before = DeckModel.Read(d.Slides);
        var looking = body.Slide is { } n && n >= 0 && n < before.Count ? n : (int?)null;
        var made = await maker.ChangeAsync(me, d.Title, before, change, looking, ct);
        if (made.Problem is not null)
        {
            return made.Problem;
        }
        var pictures = before.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is not null).GroupBy(x => x.Prompt).ToDictionary(g => g.Key, g => g.First().FileId);
        foreach (var picture in made.Slides.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is null && pictures.ContainsKey(x.Prompt)))
        {
            picture.FileId = pictures[picture.Prompt];
        }
        // A chart the model kept but whose numbers it dropped gets them back from the version before.
        var charts = before.Select(s => s.Chart).OfType<DeckChart>().Where(c => c.Ask is not null && c.Categories.Count > 0).GroupBy(c => c.Ask!.Question).ToDictionary(g => g.Key, g => g.First());
        foreach (var chart in made.Slides.Select(s => s.Chart).OfType<DeckChart>().Where(c => c.Ask is not null && c.Categories.Count == 0 && charts.ContainsKey(c.Ask.Question)))
        {
            var old = charts[chart.Ask!.Question];
            (chart.Categories, chart.Series, chart.Note) = (old.Categories, old.Series, old.Note);
        }
        await maker.CheckFilesAsync(made.Slides, me, ct);
        await maker.FillChartsAsync(me, made.Slides, false, ct);
        Save(d, db, made.Title, made.Slides, change);
        await db.SaveChangesAsync(ct);
        await canvas.FromDeckAsync(d, made.Slides, "Changed in Decks: " + (change.Length > 200 ? change[..199] + "…" : change), ct);
        return Results.Ok(Full(d, made.Slides));
    }

    /// <summary>Every chart that asks a database asked again: today's numbers.</summary>
    private static async Task<IResult> RefreshAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, SlideCanvases canvas, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(d.Slides);
        await maker.FillChartsAsync(me, slides, true, ct);
        Save(d, db, d.Title, slides, "Numbers asked again");
        await db.SaveChangesAsync(ct);
        await canvas.FromDeckAsync(d, slides, "Numbers asked again", ct);
        return Results.Ok(Full(d, slides));
    }

    /// <summary>A slide's picture made from its prompt (once: a slide that has one keeps it), kept in the deck as it is.</summary>
    private static async Task<IResult> PictureAsync(Guid id, int index, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, SlideCanvases canvas, ToolRegistry registry,
        AccessService access, ImageTool images, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(d.Slides);
        if (index < 0 || index >= slides.Count || slides[index].Picture is not { Prompt.Length: > 0 } picture)
        {
            return AuthEndpoints.Problem(400, "picture", "That slide asks for no picture.");
        }
        if (picture.FileId is not null)
        {
            return Results.Ok(Full(d, slides));
        }
        if (!await PicturesAsync(me, registry, access, images, ct))
        {
            return AuthEndpoints.Problem(409, "picture", "There is no picture model you may use: an admin adds one, or lets you use it.");
        }
        var (file, problem) = await maker.PictureAsync(me, picture.Prompt, ct);
        if (file is null)
        {
            return AuthEndpoints.Problem(502, "picture", problem ?? "The picture could not be made.");
        }
        // The deck as it is now (it may have changed while the picture was made): the picture goes to the slides that ask for it.
        var now = await db.Decks.SingleAsync(x => x.Id == id, ct);
        var current = DeckModel.Read(now.Slides);
        foreach (var asking in current.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is null && x.Prompt == picture.Prompt))
        {
            asking.FileId = file;
        }
        now.Slides = DeckModel.Write(current);
        now.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await canvas.FromDeckAsync(now, current, "A picture made", ct);
        return Results.Ok(Full(now, current));
    }

    private static async Task<IResult> VersionsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Decks.AnyAsync(d => d.Id == id && d.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await db.DeckVersions.AsNoTracking().Where(v => v.DeckId == id).OrderByDescending(v => v.Version).Take(200)
            .Select(v => new { v.Version, v.Title, v.Note, v.CreatedAt }).ToListAsync(ct));
    }

    private static async Task<IResult> RestoreAsync(Guid id, int version, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, SlideCanvases canvas, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        if (await db.DeckVersions.AsNoTracking().SingleOrDefaultAsync(v => v.DeckId == id && v.Version == version, ct) is not { } old)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(old.Slides);
        await maker.CheckFilesAsync(slides, me, ct);
        Save(d, db, old.Title, slides, $"Version {version} put back");
        await db.SaveChangesAsync(ct);
        await canvas.FromDeckAsync(d, slides, $"Deck version {version} put back", ct);
        return Results.Ok(Full(d, slides));
    }

    /// <summary>The deck as PowerPoint, in its template (or the built-in look).</summary>
    private static async Task<IResult> PptxAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.Decks.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is { } d ? await FileAsync(d, me, db, clock, ct) : Results.NotFound();
    }

    /// <summary>A deck of this person's as its PowerPoint file: the file it was opened from revised, else made in its template or look.</summary>
    public static async Task<IResult> FileAsync(Deck d, AppUser me, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var slides = DeckModel.Read(d.Slides);
        var ids = slides.Select(s => s.Picture?.FileId).OfType<Guid>().Distinct().ToList();
        var images = await db.ChatAttachments.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.UserId == me.Id && a.Data != null && (a.ContentType == "image/png" || a.ContentType == "image/jpeg" || a.ContentType == "image/gif"))
            .ToDictionaryAsync(a => a.Id, a => new DeckImage(a.Data!, a.ContentType), ct);
        var template = d.TemplateId is { } t ? await db.DeckTemplates.AsNoTracking().Where(x => x.Id == t).Select(x => x.Data).SingleOrDefaultAsync(ct) : null;
        var source = await db.DeckSources.AsNoTracking().Where(x => x.DeckId == d.Id).Select(x => x.Data).SingleOrDefaultAsync(ct);
        byte[] file;
        try
        {
            // Opened from a file: that file, revised; else made in its template or look.
            file = source is not null
                ? DeckPptx.Revise(source, d.Title, slides, images, clock.GetUtcNow(), DeckModel.ReadLook(d.Look))
                : DeckPptx.Build(d.Title, slides, images, template, clock.GetUtcNow(), DeckModel.ReadLook(d.Look));
        }
        catch (DeckTemplateException ex)
        {
            return AuthEndpoints.Problem(409, "template", $"Its template cannot be used: {ex.Message} Choose another, or the built-in look.");
        }
        var name = string.Concat(d.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' ? '-' : c)).Trim();
        return Results.File(file, "application/vnd.openxmlformats-officedocument.presentationml.presentation", (name.Length > 0 ? name : "deck") + ".pptx");
    }

    private static async Task<IResult> TemplatesAsync(AppDbContext db, CancellationToken ct)
    {
        var templates = await db.DeckTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsDefault, t.Theme, t.CreatedAt, size = t.Data.Length }).ToListAsync(ct);
        return Results.Ok(templates.Select(t => new { t.Id, t.Name, t.IsDefault, theme = JsonNode.Parse(t.Theme), t.CreatedAt, t.size }));
    }

    /// <summary>A company template (.pptx or .potx): read now (its masters, layouts and theme), so a broken one is found here.</summary>
    /// <summary>
    /// A PowerPoint file of the person's opened as a deck, to revise: its slides read (words, tables, charts, notes; each
    /// slide's first picture kept as their file), the file kept, and downloaded again as that file with only the slides they
    /// changed made anew. Its numbers are the file's own, so they are kept (no made-up check).
    /// </summary>
    private static async Task<IResult> ImportAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MostTemplateBytes + 1024 * 1024;
        }
        if (!request.HasFormContentType || (await request.ReadFormAsync(ct)) is not { Files: [var upload] })
        {
            return AuthEndpoints.Problem(400, "file", "Send one .pptx file.");
        }
        if (upload.Length is 0 or > MostTemplateBytes)
        {
            return AuthEndpoints.Problem(400, "file", "A PowerPoint file to revise is at most 30 MB.");
        }
        if (await db.Decks.CountAsync(d => d.OwnerId == me.Id, ct) >= DeckMaker.MostDecks)
        {
            return AuthEndpoints.Problem(409, "decks", $"You have {DeckMaker.MostDecks} decks, the most: remove one first.");
        }
        // Plugins' hooks before it is kept: observed, renamed by one granted to, or refused by one granted to.
        var (given, stopped) = await Plugins.PluginUploads.BeforeStoreAsync(request.HttpContext.RequestServices, me,
            Path.GetFileName(upload.FileName) is { Length: > 0 } u ? u : "deck.pptx", upload.ContentType, ct);
        if (stopped is not null)
        {
            return AuthEndpoints.Problem(400, "plugin", stopped);
        }
        using var m = new MemoryStream();
        await upload.CopyToAsync(m, ct);
        var data = m.ToArray();
        string title;
        List<ReadSlide> read;
        try
        {
            (title, read) = DeckPptx.Read(data);
        }
        catch (Exception ex) when (ex is DeckTemplateException or System.Xml.XmlException)
        {
            return AuthEndpoints.Problem(400, "file", ex is DeckTemplateException ? ex.Message : $"The file's XML does not read: {ex.Message}");
        }
        if (read.Count is 0 or > DeckModel.MostSlides)
        {
            return AuthEndpoints.Problem(400, "file", $"A deck here has 1 to {DeckModel.MostSlides} slides; that file has {read.Count}.");
        }
        foreach (var (slide, picture) in read.Where(r => r.Picture is not null))
        {
            var file = new ChatAttachment { UserId = me.Id, FileName = $"slide-{slide.Origin!.Slide}{(picture!.ContentType == "image/jpeg" ? ".jpg" : picture.ContentType == "image/gif" ? ".gif" : ".png")}",
                ContentType = picture.ContentType, Size = picture.Data.Length, Text = "", Kind = "image", Data = picture.Data, Origin = "deck" };
            db.ChatAttachments.Add(file);
            slide.Picture!.FileId = file.Id;
        }
        // Each slide as kept, with what it was when read: the same, and its slide in the file is kept as it is there.
        var slides = read.Select(r => DeckModel.Clean(r.Slide)).ToList();
        foreach (var s in slides.Where(s => s.Origin is not null))
        {
            s.Origin!.Read = DeckModel.Fingerprint(s);
        }
        title = title.Length > 200 ? title[..200] : title;
        var deck = new Deck { OwnerId = me.Id, Title = title, Slides = DeckModel.Write(slides) };
        db.Decks.Add(deck);
        var name = given.Length > 200 ? given[..200] : given;
        db.DeckSources.Add(new DeckSource { DeckId = deck.Id, FileName = name, Data = data });
        db.DeckVersions.Add(new DeckVersion { DeckId = deck.Id, Version = 1, Title = deck.Title, Slides = deck.Slides, Note = $"Opened {name}" });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/decks/{deck.Id}", Full(deck, slides, name));
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MostTemplateBytes + 1024 * 1024;
        }
        if (!request.HasFormContentType || (await request.ReadFormAsync(ct)) is not { Files: [var upload] } form)
        {
            return AuthEndpoints.Problem(400, "file", "Send one .pptx or .potx file.");
        }
        if (upload.Length is 0 or > MostTemplateBytes)
        {
            return AuthEndpoints.Problem(400, "file", "A template is at most 30 MB.");
        }
        using var m = new MemoryStream();
        await upload.CopyToAsync(m, ct);
        var data = m.ToArray();
        string theme;
        try
        {
            theme = DeckPptx.Theme(data);
            // Built once with a slide of each kind: a template that cannot carry a deck is refused now.
            DeckPptx.Build("Check", [new DeckSlide { Layout = "title", Title = "Check" }, new DeckSlide { Layout = "bullets", Title = "Check", Bullets = ["Check"] }], new Dictionary<Guid, DeckImage>(), data, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is DeckTemplateException or System.Xml.XmlException)
        {
            return AuthEndpoints.Problem(400, "file", ex is DeckTemplateException ? ex.Message : $"The template's XML does not read: {ex.Message}");
        }
        var name = (form["name"].ToString().Trim() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(upload.FileName)).Trim();
        name = name.Length > 100 ? name[..100] : name.Length == 0 ? "Template" : name;
        if (await db.DeckTemplates.AnyAsync(t => t.Name == name, ct))
        {
            return AuthEndpoints.Problem(409, "name", "Another template has that name.");
        }
        var first = !await db.DeckTemplates.AnyAsync(ct);
        var template = new DeckTemplate { Name = name, Data = data, Theme = theme, IsDefault = first };
        db.DeckTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("deck.template.add", name);
        return Results.Created($"/api/admin/deck-templates/{template.Id}", new { template.Id, template.Name, template.IsDefault, theme = JsonNode.Parse(theme), template.CreatedAt, size = data.Length });
    }

    private static async Task<IResult> ChangeTemplateAsync(Guid id, TemplateChange body, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.DeckTemplates.SingleOrDefaultAsync(t => t.Id == id, ct) is not { } template)
        {
            return Results.NotFound();
        }
        if (body.Name?.Trim() is { Length: > 0 } name)
        {
            if (await db.DeckTemplates.AnyAsync(t => t.Name == name && t.Id != id, ct))
            {
                return AuthEndpoints.Problem(409, "name", "Another template has that name.");
            }
            template.Name = name.Length > 100 ? name[..100] : name;
        }
        if (body.IsDefault is { } isDefault)
        {
            if (isDefault)
            {
                await db.DeckTemplates.Where(t => t.IsDefault && t.Id != id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);
            }
            template.IsDefault = isDefault;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("deck.template.update", template.Name);
        return Results.Ok(new { template.Id, template.Name, template.IsDefault, theme = JsonNode.Parse(template.Theme), template.CreatedAt, size = template.Data.Length });
    }

    private static async Task<IResult> RemoveTemplateAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.DeckTemplates.SingleOrDefaultAsync(t => t.Id == id, ct) is not { } template)
        {
            return Results.NotFound();
        }
        db.DeckTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("deck.template.remove", template.Name);
        return Results.NoContent();
    }
}
