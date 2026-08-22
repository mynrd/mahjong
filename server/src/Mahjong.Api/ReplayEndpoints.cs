using System.ComponentModel.DataAnnotations;
using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>Open the replays for a room by giving its password.</summary>
public sealed record ReplayUnlockRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// Reading finished hands back.
///
/// Everything here is gated on <see cref="ReplayAuth"/> rather than on holding a seat, because the
/// whole point is that somebody opens /room/CODE/replay in a browser that never played. Only hands
/// with <see cref="GameStatus.Finished"/> are readable: a hand still in progress would hand a
/// player at the table the other three hands.
/// </summary>
public static class ReplayEndpoints
{
    public static void MapReplayEndpoints(this WebApplication app)
    {
        var rooms = app.MapGroup("/api/rooms");

        rooms.MapPost("/{code}/replay/unlock", Unlock);
        rooms.MapGet("/{code}/replays", ListReplays);
        rooms.MapGet("/{code}/replays/{handNumber:int}", GetReplay);
    }

    // ------------------------------------------------------------------ unlock

    private static async Task<IResult> Unlock(
        string code,
        ReplayUnlockRequest request,
        ReplayAuth auth,
        MahjongDbContext db,
        CancellationToken cancel)
    {
        var normalised = RoomCode.Normalise(code);

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Code == normalised, cancel);
        if (room is null) return Results.NotFound(new ErrorResponse("RoomNotFound", $"No room with code {normalised}."));

        var unlocked = await auth.UnlockAsync(room, request.Password, cancel);

        return unlocked is null
            ? Results.Json(new ErrorResponse("WrongPassword"), statusCode: StatusCodes.Status401Unauthorized)
            : Results.Ok(unlocked);
    }

    // ------------------------------------------------------------------ list

    private static async Task<IResult> ListReplays(
        string code,
        HttpContext http,
        ReplayAuth auth,
        MahjongDbContext db,
        CancellationToken cancel)
    {
        var normalised = RoomCode.Normalise(code);

        var room = await auth.ResolveForRoomAsync(http, normalised, cancel);
        if (room is null) return Locked();

        var names = await NamesAsync(db, room.Id, cancel);

        var rows = await db.Games
            .Where(g => g.RoomId == room.Id && g.Status == GameStatus.Finished)
            .OrderBy(g => g.HandNumber)
            .Select(g => new
            {
                g.HandNumber,
                g.StartedAt,
                g.EndedAt,
                g.ManoSeat,
                g.JokerTile,
                Result = db.HandResults.FirstOrDefault(r => r.GameId == g.Id),
                FrameCount = db.GameFrames.Count(f => f.GameId == g.Id),
            })
            .ToListAsync(cancel);

        var items = rows.Select(row => new ReplayListItemView(
            row.HandNumber,
            row.StartedAt,
            row.EndedAt,
            row.ManoSeat,
            row.JokerTile,
            row.Result?.WinnerSeat,
            row.Result?.WinnerSeat is { } seat && names.TryGetValue(seat, out var name) ? name : null,
            row.Result?.Reason ?? nameof(HandEndReason.WallExhausted),
            row.Result?.TotalUnits ?? 0,
            row.FrameCount)).ToList();

        return Results.Ok(items);
    }

    // ------------------------------------------------------------------ one hand

    private static async Task<IResult> GetReplay(
        string code,
        int handNumber,
        HttpContext http,
        ReplayAuth auth,
        MahjongDbContext db,
        CancellationToken cancel)
    {
        var normalised = RoomCode.Normalise(code);

        var room = await auth.ResolveForRoomAsync(http, normalised, cancel);
        if (room is null) return Locked();

        var game = await db.Games
            .FirstOrDefaultAsync(g => g.RoomId == room.Id && g.HandNumber == handNumber, cancel);

        if (game is null) return Results.NotFound(new ErrorResponse("HandNotFound"));

        // A hand still being played would show the table its opponents' tiles. The list already
        // hides these, so reaching this is either a hand-typed URL or a hand that started between
        // the two calls.
        if (game.Status != GameStatus.Finished)
            return Results.Conflict(new ErrorResponse("HandInProgress", "That hand has not finished."));

        var players = await db.Players
            .Where(p => p.RoomId == room.Id)
            .Select(p => new { p.Seat, p.DisplayName, p.IsBot, p.Balance })
            .ToListAsync(cancel);

        var seatInfo = players.ToDictionary(p => p.Seat, p => (Name: p.DisplayName, p.IsBot, p.Balance));
        var names = players.ToDictionary(p => p.Seat, p => p.DisplayName);

        var actions = await db.GameActions
            .Where(a => a.GameId == game.Id)
            .OrderBy(a => a.Seq)
            .ToListAsync(cancel);

        var frames = await db.GameFrames
            .Where(f => f.GameId == game.Id)
            .OrderBy(f => f.AfterSeq)
            .ToListAsync(cancel);

        var views = new List<ReplayFrameView>(frames.Count);
        var from = 1;

        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];

            // Every action logged since the previous frame is what produced this one: a move can
            // emit several events at once (draw, then the bonus it turned up, then the ambition
            // that paid), and they all land on the same snapshot.
            var covered = actions.Where(a => a.Seq >= from && a.Seq <= frame.AfterSeq).ToList();
            from = frame.AfterSeq + 1;

            views.Add(ReplayViewBuilder.Build(
                GameJson.Deserialize<GameState>(frame.StateJson),
                index,
                frame.AfterSeq,
                ReplayCaption.For(covered, names),
                seatInfo));
        }

        return Results.Ok(new ReplayView(room.Code, game.HandNumber, game.ManoSeat, game.JokerTile, views));
    }

    // ------------------------------------------------------------------ helpers

    private static IResult Locked() =>
        Results.Json(
            new ErrorResponse("ReplayLocked", "Give the room password to read its replays."),
            statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<Dictionary<int, string>> NamesAsync(
        MahjongDbContext db, Guid roomId, CancellationToken cancel) =>
        await db.Players
            .Where(p => p.RoomId == roomId)
            .ToDictionaryAsync(p => p.Seat, p => p.DisplayName, cancel);
}
