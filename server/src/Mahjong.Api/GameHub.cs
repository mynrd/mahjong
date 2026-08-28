using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// The live connection for a seat. Everything that happens during a hand goes over this rather
/// than REST, because the server has to push: a player needs to know it is their turn without
/// asking, and a claim window is only a few seconds wide.
///
/// A connection is bound to a seat once, at connect time, from the bearer token. Hub methods
/// never take a seat number from the caller, so a client cannot act on somebody else's behalf by
/// sending a different number.
/// </summary>
public sealed class GameHub(
    PlayerAuth auth,
    RoomRegistry registry,
    GameService games,
    MahjongDbContext db,
    ILogger<GameHub> logger) : Hub
{
    private const string SeatKey = "seat";
    private const string RoomKey = "room";
    private const string PlayerKey = "player";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var token = http is null ? null : PlayerAuth.TokenFrom(http);

        var player = await auth.ResolveAsync(token);
        if (player?.Room is null)
        {
            logger.LogWarning("Rejecting hub connection {Connection}: no seat for that token.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var code = player.Room.Code;

        Context.Items[SeatKey] = player.Seat;
        Context.Items[RoomKey] = code;
        Context.Items[PlayerKey] = player.Id;

        var session = await games.RestoreAsync(code) ?? registry.GetOrCreate(player.RoomId, code);
        session.Attach(player.Seat, Context.ConnectionId);

        player.IsConnected = true;
        player.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(code));

        // A reconnecting client needs the current picture immediately, not at the next move.
        var view = await games.ViewForAsync(code, player.Seat);
        if (view is not null) await Clients.Caller.SendAsync("StateChanged", view);

        await Clients.Group(GroupFor(code)).SendAsync("SeatConnected", player.Seat, player.DisplayName);

        // A browser that still holds a token for a table that has since been closed would otherwise
        // sit on "Connecting to the table..." forever: there is no hand to build a view from and
        // nothing is ever going to push one. Told on the way in instead.
        if (player.Room.Status == RoomStatus.Closed)
            await Clients.Caller.SendAsync("TableClosed", "This table has been closed.");

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RoomKey, out var codeObj) && codeObj is string code
            && Context.Items.TryGetValue(SeatKey, out var seatObj) && seatObj is int seat)
        {
            var session = registry.Find(code);
            var lastOne = session?.Detach(seat, Context.ConnectionId) ?? true;

            if (lastOne && Context.Items.TryGetValue(PlayerKey, out var idObj) && idObj is Guid playerId)
            {
                // Losing the connection marks the seat away. It never frees the seat: the player is
                // mid-hand and holds tiles, and handing the seat to somebody else would be worse
                // than waiting for them to come back.
                var player = await db.Players.FindAsync(playerId);
                if (player is not null)
                {
                    player.IsConnected = false;
                    player.LastSeenAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                }
            }

            await Clients.Group(GroupFor(code)).SendAsync("SeatDisconnected", seat);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ------------------------------------------------------------------ moves

    public Task<Result> Draw() => Move(new GameMove.Draw());

    public Task<Result> Discard(int tileId) => Move(new GameMove.Discard(tileId));

    public Task<Result> Claim(ClaimKind kind, int[]? tileIds) => Move(new GameMove.Claim(kind, tileIds ?? []));

    public Task<Result> Pass() => Move(new GameMove.Pass());

    /// <summary>Takes back a call pressed but not yet paid for. Only ever needed with assist off.</summary>
    public Task<Result> Withdraw() => Move(new GameMove.Withdraw());

    public Task<Result> DeclareSecretKang(string face) => Move(new GameMove.SecretKang(face));

    public Task<Result> DeclareSagasa(string face) => Move(new GameMove.Sagasa(face));

    public Task<Result> DeclareTodas() => Move(new GameMove.Todas());

    // ------------------------------------------------------------------ after the hand

    /// <summary>
    /// Turns this seat's hand face up for the other three, once the hand is over. Not a move: it
    /// changes nothing the rules can see, which is why it does not go through <see cref="Move"/>.
    /// </summary>
    public async Task<Result> Reveal()
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[SeatKey] is not int seat)
            return Result.Fail("NotSeated");

        return await games.RevealAsync(code, seat);
    }

    // ------------------------------------------------------------------ the next game
    //
    // Calling a game does not deal one: everybody answers for themselves and the caller waits with
    // them. All six of these go over the hub rather than REST because the whole point is that the
    // other three watch the answers arrive.

    /// <summary>Host only: offers another game to the table.</summary>
    public Task<Result> ProposeNewGame() => AsHost(games.ProposeNewGameAsync);

    /// <summary>Host only: takes the offer back.</summary>
    public Task<Result> CancelNewGame() => AsHost(games.CancelNewGameAsync);

    /// <summary>Host only: sits a bot in every seat still empty.</summary>
    public Task<Result> FillWithBots() => AsHost(games.FillSeatsWithBotsAsync);

    /// <summary>
    /// Host only: ends the table for everybody, mid-hand or between hands.
    ///
    /// Not a move and not a vote. The seat that made the table is the one that gets to say it is
    /// over, and there is nothing for the other three to answer: a table nobody is dealing at is
    /// already finished, the button only says so.
    /// </summary>
    public Task<Result> CloseTable() => AsHost(games.CloseTableAsync);

    /// <summary>Host only: frees the seat of somebody who has stopped answering, or a bot.</summary>
    public async Task<Result> RemoveSeat(int seat)
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[PlayerKey] is not Guid playerId)
            return Result.Fail("NotSeated");

        return await games.RemoveSeatAsync(code, playerId, seat);
    }

    /// <summary>Says yes to the offer.</summary>
    public async Task<Result> AcceptNewGame()
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[SeatKey] is not int seat)
            return Result.Fail("NotSeated");

        return await games.AcceptNewGameAsync(code, seat);
    }

    /// <summary>
    /// Says no, and leaves. Saying no to another game is leaving the table: there is no third state
    /// where somebody sits at a table they have declined to play at.
    /// </summary>
    public async Task<Result> DeclineNewGame()
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[PlayerKey] is not Guid playerId)
            return Result.Fail("NotSeated");

        return await games.LeaveTableAsync(code, playerId);
    }

    private async Task<Result> AsHost(Func<string, Guid, CancellationToken, Task<Result>> action)
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[PlayerKey] is not Guid playerId)
            return Result.Fail("NotSeated");

        return await action(code, playerId, CancellationToken.None);
    }

    // ------------------------------------------------------------------ hand arrangement
    //
    // How this seat has laid its own tiles out. Not a move and not rules state: nothing here
    // reaches GameState, no other seat is told, and a wrong value changes nothing but the drawing
    // on one screen. It lives on the server only so that a phone that sleeps mid-hand does not come
    // back having lost the grouping the player built by hand.

    /// <summary>Groups of tile ids, in the order the player put them. Empty when nothing is set.</summary>
    public async Task<int[][]> GetArrangement()
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[PlayerKey] is not Guid playerId)
            return [];

        var gameId = registry.Find(code)?.GameId;
        if (gameId is null) return [];

        var row = await db.HandArrangements
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.GameId == gameId && a.PlayerId == playerId);

        if (row is null) return [];

        try
        {
            return JsonSerializer.Deserialize<int[][]>(row.GroupsJson) ?? [];
        }
        catch (JsonException)
        {
            // Nothing else reads this column, so bad JSON in it can only mean a hand-edited row.
            // The arrangement is cosmetic, so losing it beats failing the connection.
            logger.LogWarning("Unreadable arrangement for player {Player} in game {Game}.", playerId, gameId);
            return [];
        }
    }

    public async Task<Result> SaveArrangement(int[][]? groups)
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[PlayerKey] is not Guid playerId)
            return Result.Fail("NotSeated");

        var gameId = registry.Find(code)?.GameId;
        if (gameId is null) return Result.Fail("NoHand");

        var clean = Sanitise(groups);
        if (clean is null) return Result.Fail("BadArrangement");

        var row = await db.HandArrangements
            .FirstOrDefaultAsync(a => a.GameId == gameId && a.PlayerId == playerId);

        if (row is null)
        {
            row = new HandArrangement { GameId = gameId.Value, PlayerId = playerId };
            db.HandArrangements.Add(row);
        }

        row.GroupsJson = JsonSerializer.Serialize(clean);
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        return Result.Ok();
    }

    /// <summary>
    /// Caps what a client can push into a text column, and drops the shapes that are meaningless
    /// rather than rejecting the save over them. A tile id that is not in the player's hand is left
    /// alone: the client filters on load, and checking here would need the hand, which this method
    /// deliberately does not touch. Null means "refuse", not "empty".
    /// </summary>
    private static int[][]? Sanitise(int[][]? groups)
    {
        if (groups is null) return [];
        if (groups.Length > MaxGroups) return null;

        var seen = new HashSet<int>();
        var kept = new List<int[]>();

        foreach (var group in groups)
        {
            if (group is null) return null;

            // A group of one is just a loose tile, and the client dissolves those anyway.
            var ids = group.Where(seen.Add).ToArray();
            if (ids.Length > 1) kept.Add(ids);

            if (seen.Count > MaxTiles) return null;
        }

        return kept.ToArray();
    }

    /// <summary>A hand is 17 tiles at its largest. The caps are slack on purpose, not tight.</summary>
    private const int MaxGroups = 20;
    private const int MaxTiles = 24;

    private async Task<Result> Move(GameMove move)
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[SeatKey] is not int seat)
            return Result.Fail("NotSeated");

        var result = await games.MoveAsync(code, seat, move);

        if (!result.Success)
            logger.LogDebug("Seat {Seat} in {Code} tried {Move}: {Error} {Detail}",
                seat, code, move.GetType().Name, result.Error, result.Detail);

        return result;
    }

    public static string GroupFor(string code) => $"room:{code.ToUpperInvariant()}";
}
