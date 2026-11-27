// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Notifications;

/// <summary>The bell: a person's news (a workflow's, an answer written while nobody watched, credit, the system's alerts), read and cleared.</summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var n = app.MapGroup("/api/notifications").PersonOnly();
        n.MapGet("", NotificationsAsync);
        n.MapPost("/{id:guid}/read", ReadAsync);
        n.MapPost("/read", ReadAllAsync);
        n.MapDelete("/{id:guid}", ClearAsync);
        n.MapDelete("", ClearAllAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static async Task<IResult> NotificationsAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        // One snapshot for both, so the count never tells of news the list does not have yet.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var items = await db.Notifications.AsNoTracking().Where(x => x.UserId == me.Id && !x.Cleared).OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(x => x.UserId == me.Id && x.ReadAt == null && !x.Cleared, ct);
        await tx.CommitAsync(ct);
        // Plain words, as the bell shows them (news kept before it was made plain too).
        return Results.Ok(new
        {
            unread,
            items = items.Select(x => new { x.Id, x.Kind, title = NewsText.Plain(x.Title, 300) ?? x.Kind, body = NewsText.Plain(x.Body), x.Link, x.CreatedAt, read = x.ReadAt != null }),
        });
    }

    private static async Task<IResult> ReadAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var now = clock.GetUtcNow();
        return await db.Notifications.Where(x => x.Id == id && x.UserId == me.Id && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct) > 0
            || await db.Notifications.AnyAsync(x => x.Id == id && x.UserId == me.Id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> ReadAllAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var now = clock.GetUtcNow();
        await db.Notifications.Where(x => x.UserId == me.Id && x.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
        // The last hundred are kept; news said once (an alert's firing, a credit threshold) a month, so it is not said again meanwhile.
        var month = now.AddDays(-30);
        var old = await db.Notifications.Where(x => x.UserId == me.Id).OrderByDescending(x => x.CreatedAt).Skip(100)
            .Where(x => x.Key == null || x.CreatedAt < month).Select(x => x.Id).ToListAsync(ct);
        if (old.Count > 0)
        {
            await db.Notifications.Where(x => old.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }
        return Results.NoContent();
    }

    /// <summary>Clears one: gone, or (news said once) hidden, so it is not said again.</summary>
    private static async Task<IResult> ClearAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        var mine = db.Notifications.Where(x => x.Id == id && x.UserId == me.Id);
        if (!await mine.AnyAsync(ct))
        {
            return Results.NotFound();
        }
        await ClearWhereAsync(mine, clock.GetUtcNow(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearAllAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        await ClearWhereAsync(db.Notifications.Where(x => x.UserId == me.Id), clock.GetUtcNow(), ct);
        return Results.NoContent();
    }

    private static async Task ClearWhereAsync(IQueryable<Notification> which, DateTimeOffset now, CancellationToken ct)
    {
        await which.Where(x => x.Key == null).ExecuteDeleteAsync(ct);
        await which.Where(x => x.Key != null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Cleared, true).SetProperty(x => x.ReadAt, x => x.ReadAt ?? now), ct);
    }
}
