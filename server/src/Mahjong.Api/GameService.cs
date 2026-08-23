using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// Drives hands: starts them, applies moves through the rules engine, writes the result down and
/// pushes each player their own redacted view.
///
/// Every public method that changes a hand runs inside <see cref="RoomSession.RunAsync{T}"/>, so
/// the rules engine only ever sees one caller at a time.
/// </summary>
public sealed class GameService(
    MahjongDbContext db,
    RoomRegistry registry,
    IHubContext<GameHub> hub,
    TimeProvider clock,
    ILogger<GameService> logger)
{
    /// <summary>Deals the next hand for a room. Only the host may trigger this.</summary>
    public async Task<Result> StartHandAsync(string code, Guid callerPlayerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId) return Result.Fail("HostOnly");

        var humansAndBots = room.Players.Count;
        if (humansAndBots < MahjongGame.Seats)
            return Result.Fail("NotEnoughPlayers", $"{humansAndBots} of {MahjongGame.Seats} seats are taken.");

        var session = registry.GetOrCreate(room.Id, room.Code);

        return await session.RunAsync(async () =>
        {
            if (session.State is { Phase: not GamePhase.HandOver })
                return Result.Fail("HandInProgress");

            var rules = GameJson.Deserialize<RuleOptions>(room.RulesJson);
            var handNumber = room.HandsPlayed + 1;
            var mano = await NextManoSeatAsync(room, rules, cancel);
            var seed = Random.Shared.Next();

            var (state, events) = MahjongGame.Deal(rules, handNumber, mano, seed, clock.GetUtcNow());

            // Which seats are bots is a fact about the room, but the rules need it: an assist-off
            // window on a bot's discard is the one that gets a deadline. Stamped in before the
            // state is serialised, so it survives a restart mid-hand along with everything else.
            foreach (var bot in room.Players.Where(p => p.IsBot)) state.BotSeats.Add(bot.Seat);

            var game = new Game
            {
                RoomId = room.Id,
                HandNumber = handNumber,
                ManoSeat = mano,
                JokerTile = state.Joker?.Code,
                Seed = seed,
                StateJson = GameJson.Serialize(state),
            };

            db.Games.Add(game);
            room.Status = RoomStatus.Playing;

            session.State = state;
            session.GameId = game.Id;
            session.NextSeq = 1;

            await RecordAsync(session, room, game, events, cancel);
            await db.SaveChangesAsync(cancel);

            await BroadcastAsync(session, room, cancel);
            return Result.Ok();
        }, cancel);
    }

    /// <summary>Applies one move by one seat, whatever kind it is.</summary>
    public async Task<Result> MoveAsync(string code, int seat, GameMove move, CancellationToken cancel = default)
    {
        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        return await session.RunAsync(async () =>
        {
            if (session.State is not { } state) return Result.Fail("NoHandInProgress");
            if (state.Phase == GamePhase.HandOver) return Result.Fail("HandOver");

            List<GameEvent> events;
            var now = clock.GetUtcNow();

            try
            {
                events = move switch
                {
                    GameMove.Draw => MahjongGame.Draw(state, seat, now),
                    GameMove.Discard d => MahjongGame.Discard(state, seat, d.TileId, now),
                    GameMove.Claim c => MahjongGame.Claim(state, seat, c.Kind, c.TileIds, now),
                    GameMove.Pass => MahjongGame.Pass(state, seat, now),
                    GameMove.SecretKang k => MahjongGame.DeclareSecretKang(state, seat, Tile.Parse(k.Face)),
                    GameMove.Sagasa s => MahjongGame.DeclareSagasa(state, seat, Tile.Parse(s.Face)),
                    GameMove.Todas => MahjongGame.DeclareTodasOnDraw(state, seat),
                    _ => throw new IllegalMoveException($"Unknown move {move.GetType().Name}."),
                };
            }
            catch (IllegalMoveException ex)
            {
                // An illegal move is a normal thing for a client to send: two players can both tap
                // Pung and only one can win. It is reported back, not logged as a fault.
                return Result.Fail("IllegalMove", ex.Message);
            }

            var game = await db.Games.FirstAsync(g => g.Id == session.GameId, cancel);

            await RecordAsync(session, room, game, events, cancel);
            await db.SaveChangesAsync(cancel);
            await BroadcastAsync(session, room, cancel);

            return Result.Ok();
        }, cancel);
    }

    /// <summary>
    /// Closes a claim window whose deadline has passed. Called by the ticker rather than a timer,
    /// so a window that was open when the process restarted still gets closed.
    /// </summary>
    public async Task ExpireClaimsAsync(RoomSession session, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(session.Code, cancel);
        if (room is null) return;

        await session.RunAsync(async () =>
        {
            if (session.State is not { Phase: GamePhase.AwaitingClaims } state) return;

            var now = clock.GetUtcNow();

            // NextDeadline covers both clocks a window can be running: its own, and the ten seconds
            // each seat that pressed pung or kang has to name the tiles. Nothing due, nothing to do.
            if (state.Pending is not { NextDeadline: { } due } || due > now) return;

            var events = MahjongGame.ExpireClaimWindow(state, now);
            if (events.Count == 0) return;

            var game = await db.Games.FirstAsync(g => g.Id == session.GameId, cancel);

            await RecordAsync(session, room, game, events, cancel);
            await db.SaveChangesAsync(cancel);
            await BroadcastAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>Rebuilds an in-memory session from the database, for a reconnect after a restart.</summary>
    public async Task<RoomSession?> RestoreAsync(string code, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return null;

        var session = registry.GetOrCreate(room.Id, room.Code);
        if (session.State is not null) return session;

        var game = await db.Games
            .Where(g => g.RoomId == room.Id && g.Status == GameStatus.InProgress)
            .OrderByDescending(g => g.HandNumber)
            .FirstOrDefaultAsync(cancel);

        if (game is null) return session;

        session.State = GameJson.Deserialize<GameState>(game.StateJson);
        session.GameId = game.Id;
        session.NextSeq = await db.GameActions.CountAsync(a => a.GameId == game.Id, cancel) + 1;

        return session;
    }

    /// <summary>Sends every connected seat its own view of the table.</summary>
    public async Task BroadcastAsync(RoomSession session, Room? room = null, CancellationToken cancel = default)
    {
        room ??= await LoadRoomAsync(session.Code, cancel);
        if (room is null || session.State is not { } state) return;

        var seatInfo = SeatInfo(room, session);

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var connections = session.ConnectionsFor(seat);
            if (connections.Count == 0) continue;

            var view = GameViewBuilder.Build(state, room.Code, seat, seatInfo);
            await hub.Clients.Clients(connections).SendAsync("StateChanged", view, cancel);
        }
    }

    /// <summary>Builds the view for one seat without broadcasting, for a client that just connected.</summary>
    public async Task<PlayerGameView?> ViewForAsync(string code, int seat, CancellationToken cancel = default)
    {
        var session = await RestoreAsync(code, cancel);
        if (session?.State is not { } state) return null;

        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return null;

        return GameViewBuilder.Build(state, room.Code, seat, SeatInfo(room, session));
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// Writes the events of one move to the log, moves any money they caused, and snapshots the
    /// state. The log and the snapshot are both kept: the log is what makes a disputed hand
    /// reconstructible, the snapshot is what makes a reconnect a single read.
    /// </summary>
    private async Task RecordAsync(
        RoomSession session,
        Room room,
        Game game,
        IReadOnlyList<GameEvent> events,
        CancellationToken cancel)
    {
        var bySeat = room.Players.ToDictionary(p => p.Seat);

        foreach (var evt in events)
        {
            db.GameActions.Add(new GameAction
            {
                GameId = game.Id,
                Seq = session.NextSeq++,
                Seat = evt.Seat,
                PlayerId = evt.Seat >= 0 && bySeat.TryGetValue(evt.Seat, out var actor) ? actor.Id : null,
                ActionType = evt.GetType().Name,
                PayloadJson = GameJson.SerializeEvent(evt),
                CreatedAt = clock.GetUtcNow(),
            });

            switch (evt)
            {
                case AmbitionEarned ambition:
                    ApplySettlements(game, null, ambition.Settlements, bySeat);
                    break;

                case HandEnded ended:
                    await CloseHandAsync(session, room, game, ended, bySeat, cancel);
                    break;
            }
        }

        if (session.State is not { } state) return;

        game.StateJson = GameJson.Serialize(state);

        // The same string, kept a second time under the action it followed. Games.StateJson is
        // overwritten on every move and so only ever holds the final position; this row is what
        // makes the hand steppable afterwards. Serialising is already paid for above.
        //
        // Only when the move actually logged something. A pass that leaves the claim window open
        // produces no events, so NextSeq has not moved, and writing a frame here would collide with
        // the previous one on the unique (GameId, AfterSeq) index and fail the whole move. Nothing
        // is lost: a pass that changes no event changes nothing a replay shows.
        if (events.Count == 0) return;

        db.GameFrames.Add(new GameFrame
        {
            GameId = game.Id,
            AfterSeq = session.NextSeq - 1,
            StateJson = game.StateJson,
            CreatedAt = clock.GetUtcNow(),
        });
    }

    private async Task CloseHandAsync(
        RoomSession session,
        Room room,
        Game game,
        HandEnded ended,
        IReadOnlyDictionary<int, Player> bySeat,
        CancellationToken cancel)
    {
        var outcome = ended.Outcome;

        var result = new HandResult
        {
            GameId = game.Id,
            WinnerSeat = outcome.WinnerSeat,
            WinnerPlayerId = outcome.WinnerSeat is { } seat && bySeat.TryGetValue(seat, out var winner) ? winner.Id : null,
            Reason = outcome.Reason.ToString(),
            TotalUnits = outcome.Score?.TotalUnits ?? 0,
            BreakdownJson = GameJson.Serialize(new
            {
                outcome.Score?.BaseUnits,
                outcome.Score?.Bonuses,
                outcome.Score?.TotalUnits,
                Wait = outcome.Score?.Wait.Select(t => t.Code),
                Reading = outcome.Score?.Reading.Sets.Select(s => s.ToString()),
            }),
            CreatedAt = clock.GetUtcNow(),
        };

        db.HandResults.Add(result);
        ApplySettlements(game, result, outcome.Settlements, bySeat);

        game.Status = GameStatus.Finished;
        game.EndedAt = clock.GetUtcNow();

        room.HandsPlayed++;
        room.Status = RoomStatus.Lobby;

        logger.LogInformation(
            "Room {Code} hand {Hand} ended: {Reason}, winner seat {Winner}, {Units} units.",
            room.Code, game.HandNumber, outcome.Reason, outcome.WinnerSeat, result.TotalUnits);

        await Task.CompletedTask;
    }

    private void ApplySettlements(
        Game game,
        HandResult? result,
        IReadOnlyList<Settlement> settlements,
        IReadOnlyDictionary<int, Player> bySeat)
    {
        foreach (var settlement in settlements)
        {
            if (!bySeat.TryGetValue(settlement.Seat, out var player)) continue;

            player.Balance += settlement.Delta;

            db.Settlements.Add(new SettlementRow
            {
                GameId = game.Id,
                HandResult = result,
                PlayerId = player.Id,
                Seat = settlement.Seat,
                Delta = settlement.Delta,
                Reason = settlement.Reason,
                CreatedAt = clock.GetUtcNow(),
            });
        }
    }

    // ------------------------------------------------------------------ helpers

    private Task<Room?> LoadRoomAsync(string code, CancellationToken cancel) =>
        db.Rooms
            .Include(r => r.Players)
            .FirstOrDefaultAsync(r => r.Code == RoomCode.Normalise(code), cancel);

    /// <summary>
    /// Who deals the next hand. The mano keeps the deal after winning, and after a drawn hand if
    /// the table plays it that way; otherwise it moves on one seat.
    /// </summary>
    private async Task<int> NextManoSeatAsync(Room room, RuleOptions rules, CancellationToken cancel)
    {
        var last = await db.Games
            .Where(g => g.RoomId == room.Id && g.Status == GameStatus.Finished)
            .OrderByDescending(g => g.HandNumber)
            .Select(g => new { g.ManoSeat, g.Id })
            .FirstOrDefaultAsync(cancel);

        if (last is null) return 0;

        var result = await db.HandResults.FirstOrDefaultAsync(r => r.GameId == last.Id, cancel);

        var manoKeeps = result switch
        {
            null => rules.ManoKeepsSeatOnDraw,
            { WinnerSeat: null } => rules.ManoKeepsSeatOnDraw,
            { WinnerSeat: var winner } => winner == last.ManoSeat,
        };

        return manoKeeps ? last.ManoSeat : GameState.NextSeat(last.ManoSeat);
    }

    private static Dictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> SeatInfo(
        Room room, RoomSession session) =>
        room.Players.ToDictionary(
            p => p.Seat,
            p => (p.DisplayName, p.IsBot, p.IsBot || session.ConnectionsFor(p.Seat).Count > 0, p.Balance));
}

/// <summary>One thing a player can ask the server to do during a hand.</summary>
public abstract record GameMove
{
    public sealed record Draw : GameMove;
    public sealed record Discard(int TileId) : GameMove;
    public sealed record Claim(ClaimKind Kind, IReadOnlyList<int> TileIds) : GameMove;
    public sealed record Pass : GameMove;
    public sealed record SecretKang(string Face) : GameMove;
    public sealed record Sagasa(string Face) : GameMove;
    public sealed record Todas : GameMove;
}

/// <summary>
/// Success or a named failure. Moves fail for ordinary reasons all the time (somebody else got the
/// tile first), so failure is a return value here rather than an exception.
/// </summary>
public sealed record Result(bool Success, string? Error = null, string? Detail = null)
{
    public static Result Ok() => new(true);
    public static Result Fail(string error, string? detail = null) => new(false, error, detail);
}
