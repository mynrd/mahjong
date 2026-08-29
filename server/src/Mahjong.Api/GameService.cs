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
    ILogger<GameService> logger,
    IConfiguration config)
{
    // A tile called off the pool is already face up in the claimer's melds, so drawing a greyed
    // copy of it in the middle as well shows the same tile in two places. Off by default. Set
    // Mahjong:ShowClaimedDiscards to true in appsettings.json and restart the server to get the
    // greyed copies back - read once at construction, so editing the file mid-game does nothing.
    private readonly bool showClaimedDiscards = config.GetValue("Mahjong:ShowClaimedDiscards", false);

    /// <summary>Deals the next hand for a room. Only the host may trigger this.</summary>
    public async Task<Result> StartHandAsync(string code, Guid callerPlayerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId) return Result.Fail("HostOnly");

        if (room.Status == RoomStatus.Closed)
            return Result.Fail("RoomClosed", "This table has been closed.");

        var humansAndBots = room.Players.Count;
        if (humansAndBots < MahjongGame.Seats)
            return Result.Fail("NotEnoughPlayers", $"{humansAndBots} of {MahjongGame.Seats} seats are taken.");

        var session = registry.GetOrCreate(room.Id, room.Code);

        return await session.RunAsync(async () =>
        {
            if (session.State is { Phase: not GamePhase.HandOver })
                return Result.Fail("HandInProgress");

            return await DealAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>
    /// Deals, with the session gate already held and the checks already made.
    ///
    /// Two ways in: the host pressing Start from the lobby, and four seats agreeing to another game
    /// at a table that has just finished one. They deal the same hand by the same route, which is
    /// why this is one method and not two that drift apart.
    /// </summary>
    private async Task<Result> DealAsync(RoomSession session, Room room, CancellationToken cancel)
    {
        // What the session was pointing at before this deal touched it. A deal writes the new hand
        // onto the session before it writes it to the database, because the log it writes is built
        // from the session as it goes - so anything throwing in between would otherwise leave a
        // live table pointing at a hand that was never saved, which the ticker then tries to move
        // bots in, over and over. Put back on the way out of a failure.
        var wasState = session.State;
        var wasGameId = session.GameId;
        var wasSeq = session.NextSeq;

        try
        {
            return await DealCoreAsync(session, room, cancel);
        }
        catch
        {
            session.State = wasState;
            session.GameId = wasGameId;
            session.NextSeq = wasSeq;
            throw;
        }
    }

    private async Task<Result> DealCoreAsync(RoomSession session, Room room, CancellationToken cancel)
    {
        var rules = GameJson.Deserialize<RuleOptions>(room.RulesJson);
        var handNumber = room.HandsPlayed + 1;
        var mano = await NextManoSeatAsync(room, rules, cancel);
        var seed = Random.Shared.Next();

        var (state, events) = MahjongGame.Deal(rules, handNumber, mano, seed, clock.GetUtcNow());

        // Which seats are bots is a fact about the room, but the rules need it: a bot holding
        // nothing that could take a discard is answered for where a person never is. Stamped in
        // before the state is serialised, so it survives a restart mid-hand with everything else.
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

        // Showing your hand lasts as long as the hand it belonged to. A new deal takes every
        // seat back to face down, whatever they chose to do with the last one.
        session.ClearReveals();

        // The offer has been taken up. Left standing, it would still be on screen over the top
        // of the hand it asked for.
        session.ClearProposal();

        await RecordAsync(session, room, game, events, cancel);
        await db.SaveChangesAsync(cancel);

        await BroadcastAsync(session, room, cancel);
        return Result.Ok();
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
                    GameMove.Withdraw => MahjongGame.Withdraw(state, seat),
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
                return Result.Fail(ex.Code ?? "IllegalMove", ex.Message);
            }

            var game = await db.Games.FirstAsync(g => g.Id == session.GameId, cancel);

            await RecordAsync(session, room, game, events, cancel);
            await db.SaveChangesAsync(cancel);
            await BroadcastAsync(session, room, cancel);

            return Result.Ok();
        }, cancel);
    }

    /// <summary>
    /// Turns one seat's hand face up for the rest of the table, now that the hand is over.
    ///
    /// One way on purpose. The other three have already seen the tiles by the time anybody could
    /// press it again, so an undo would only make the table flicker; and a hand that can be hidden
    /// again is one people would argue about afterwards. The next deal clears it.
    /// </summary>
    public async Task<Result> RevealAsync(string code, int seat, CancellationToken cancel = default)
    {
        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        return await session.RunAsync(async () =>
        {
            if (session.State is not { } state) return Result.Fail("NoHandInProgress");

            // Mid-hand this would hand the table thirteen tiles nobody is entitled to. The view
            // builder refuses it too; this is the half that gives the player a reason why.
            if (state.Phase != GamePhase.HandOver)
                return Result.Fail("HandInProgress", "You can only show your tiles once the hand is over.");

            if (session.RevealedSeats.Contains(seat)) return Result.Ok();

            session.Reveal(seat);
            await BroadcastAsync(session, room, cancel);

            return Result.Ok();
        }, cancel);
    }

    // ------------------------------------------------------------------ the next game
    //
    // A finished hand leaves four people sitting at a table, not a queue waiting to be dealt to.
    // The seat that made the table calls the next game and everybody answers for themselves; the
    // caller waits with the rest. Nothing here deals until all four seats have said yes.

    /// <summary>Calls another game. Only the host may, and only once the hand is over.</summary>
    public async Task<Result> ProposeNewGameAsync(string code, Guid callerPlayerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId)
            return Result.Fail("HostOnly", "Only the seat that made the table can call the next game.");

        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        return await session.RunAsync(async () =>
        {
            if (session.State is not { Phase: GamePhase.HandOver })
                return Result.Fail("HandInProgress", "The hand being played has to finish first.");

            var host = room.Players.FirstOrDefault(p => p.Id == callerPlayerId);
            if (host is null) return Result.Fail("NotSeated");

            // Bots are in from the start. There is no screen to ask one on, and a table waiting for
            // an answer that can never come would never deal again.
            session.Propose(NewGameProposal.Open(host.Seat, room.Players.Where(p => p.IsBot).Select(p => p.Seat)));

            return await SettleAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>Takes the offer back. The host's own way out of a proposal nobody is answering.</summary>
    public async Task<Result> CancelNewGameAsync(string code, Guid callerPlayerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId)
            return Result.Fail("HostOnly", "Only the seat that made the table can take the offer back.");

        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        return await session.RunAsync(async () =>
        {
            session.ClearProposal();
            await BroadcastAsync(session, room, cancel);
            return Result.Ok();
        }, cancel);
    }

    /// <summary>Says yes to the offer. Deals on the spot if it was the last seat outstanding.</summary>
    public async Task<Result> AcceptNewGameAsync(string code, int seat, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        return await session.RunAsync(async () =>
        {
            if (session.Proposal is null) return Result.Fail("NoProposal", "Nobody has called a new game.");

            session.Accept(seat);
            return await SettleAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>
    /// Says no, which is leaving: a player who does not want another game is not at the table for
    /// one. The seat is freed on the spot so the host can fill it, and the offer stays standing for
    /// whoever sits down next.
    /// </summary>
    public async Task<Result> LeaveTableAsync(string code, Guid playerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        var player = room.Players.FirstOrDefault(p => p.Id == playerId);
        if (player is null) return Result.Fail("NotSeated");

        // The host leaving would take with it the only seat that can call a game or fill an empty
        // one, and strand everybody still sitting there. Taking the offer back is the way out.
        if (room.HostPlayerId == playerId)
            return Result.Fail("HostCannotLeave", "You made this table. Take the offer back instead of leaving it.");

        return await session.RunAsync(async () =>
        {
            await RemoveAsync(session, room, player, "You left the table.", cancel);
            return await SettleAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>
    /// Takes somebody out of a seat: a bot the host filled with, or a player who has stopped
    /// answering. Host only, and never the host's own chair.
    ///
    /// The same method serves the lobby and the table between hands, because it is the same act
    /// either way and the checks that matter - who is asking, and whether a hand is being played -
    /// do not change with the screen it was asked from. It is deliberately refused mid-hand: that
    /// seat is holding tiles the rules are still counting, and taking the player out from under
    /// them would leave a hand nobody can finish.
    /// </summary>
    public async Task<Result> RemoveSeatAsync(string code, Guid callerPlayerId, int seat, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId)
            return Result.Fail("HostOnly", "Only the seat that made the table can remove somebody from it.");

        var player = room.Players.FirstOrDefault(p => p.Seat == seat);
        if (player is null) return Result.Fail("SeatEmpty", "Nobody is sitting there.");

        if (player.Id == callerPlayerId)
            return Result.Fail("CannotRemoveHost", "You cannot remove yourself from your own table.");

        // Created rather than looked up: a table still in the lobby has never had a session, and
        // one with no hand in it costs nothing - the ticker skips a session holding no state.
        var session = registry.GetOrCreate(room.Id, room.Code);

        return await session.RunAsync(async () =>
        {
            if (session.State is { Phase: not GamePhase.HandOver })
                return Result.Fail("HandInProgress", "You cannot take somebody out of a hand being played.");

            await RemoveAsync(session, room, player, "The host removed you from the table.", cancel);

            // Nothing to settle or broadcast at a table that has never dealt: SettleAsync builds
            // its view off a state that is not there yet, and the removal above has already been
            // written. Everybody still in the lobby finds out on their next poll.
            if (session.State is null) return Result.Ok();

            return await SettleAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>
    /// Ends the table for everybody. Host only.
    ///
    /// Unlike every other way a hand stops, this one does not produce a result: nobody declared and
    /// the wall did not run out, so there is nothing to score and nothing to settle. The hand in
    /// progress is marked abandoned rather than finished, which keeps it out of the replay list -
    /// a hand with no ending is not one anybody can step through to a conclusion.
    ///
    /// The room row stays, and so do the players and every finished hand: closing a table is not
    /// deleting it, and the replays are the reason people look at it afterwards.
    /// </summary>
    public async Task<Result> CloseTableAsync(string code, Guid callerPlayerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId)
            return Result.Fail("HostOnly", "Only the seat that made the table can close it.");

        if (room.Status == RoomStatus.Closed) return Result.Ok();

        var session = registry.GetOrCreate(room.Id, room.Code);

        return await session.RunAsync(async () =>
        {
            if (session.GameId is { } gameId)
            {
                var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId, cancel);

                if (game is { Status: GameStatus.InProgress })
                {
                    game.Status = GameStatus.Abandoned;
                    game.EndedAt = clock.GetUtcNow();
                }
            }

            room.Status = RoomStatus.Closed;

            // The table is over, so nothing is left for the ticker to move: no bots to play, no
            // claim window to expire. Cleared before the row is saved rather than after, so a tick
            // landing between the two finds a session with nothing in it.
            session.State = null;
            session.GameId = null;
            session.NextSeq = 1;
            session.ClearProposal();
            session.ClearReveals();

            await db.SaveChangesAsync(cancel);

            await hub.Clients.Group(GameHub.GroupFor(room.Code))
                .SendAsync("TableClosed", "The host closed this table.", cancel);

            logger.LogInformation("Room {Code} was closed by its host.", room.Code);

            return Result.Ok();
        }, cancel);
    }

    /// <summary>
    /// Sits a bot in every empty seat. Host only. The bots agree as they sit down, so filling the
    /// last empty seat of an otherwise agreed table deals immediately - which is the point of doing
    /// it from here rather than sending four people back to the lobby to do it.
    /// </summary>
    public async Task<Result> FillSeatsWithBotsAsync(string code, Guid callerPlayerId, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(code, cancel);
        if (room is null) return Result.Fail("RoomNotFound");

        if (room.HostPlayerId != callerPlayerId)
            return Result.Fail("HostOnly", "Only the seat that made the table can add bots to it.");

        var session = registry.Find(code);
        if (session is null) return Result.Fail("NoHandInProgress");

        return await session.RunAsync(async () =>
        {
            var seated = Roster.SeatBots(db, room);
            if (seated.Count == 0) return Result.Fail("NoFreeSeats", "Every seat is taken.");

            foreach (var bot in seated) session.Accept(bot.Seat);

            await db.SaveChangesAsync(cancel);
            return await SettleAsync(session, room, cancel);
        }, cancel);
    }

    /// <summary>
    /// Deals if every seat is taken and every seat has agreed, and otherwise just tells the table
    /// where the offer has got to. Every path that could have been the last answer ends here.
    /// </summary>
    private async Task<Result> SettleAsync(RoomSession session, Room room, CancellationToken cancel)
    {
        var occupied = room.Players.Select(p => p.Seat).ToHashSet();

        if (session.Proposal is { } proposal && proposal.IsAgreedBy(occupied))
            return await DealAsync(session, room, cancel);

        await db.SaveChangesAsync(cancel);
        await BroadcastAsync(session, room, cancel);

        return Result.Ok();
    }

    /// <summary>
    /// Takes a player out of the room, and tells them so before their token stops working.
    ///
    /// The row goes rather than being flagged: a seat has to be genuinely free for somebody else to
    /// join it, and the unique index on (room, seat) is what makes that safe. Their settlements and
    /// actions stay behind - those rows carry the seat as well as the player, so a finished hand
    /// still replays correctly with nobody sitting in that chair any more.
    /// </summary>
    private async Task RemoveAsync(RoomSession session, Room room, Player player, string reason, CancellationToken cancel)
    {
        var connections = session.ConnectionsFor(player.Seat);

        db.Players.Remove(player);
        room.Players.Remove(player);
        session.Vacate(player.Seat);

        await db.SaveChangesAsync(cancel);

        if (connections.Count > 0)
            await hub.Clients.Clients(connections).SendAsync("Removed", reason, cancel);

        logger.LogInformation("Seat {Seat} left room {Code}: {Reason}", player.Seat, room.Code, reason);
    }

    /// <summary>
    /// Awards the discard to the call standing on it, once the beat for calling over that one has
    /// passed. Called by the ticker rather than a timer, so a call left standing when the process
    /// restarted is still resolved.
    /// </summary>
    public async Task ExpireClaimsAsync(RoomSession session, CancellationToken cancel = default)
    {
        var room = await LoadRoomAsync(session.Code, cancel);
        if (room is null) return;

        await session.RunAsync(async () =>
        {
            if (session.State is not { Phase: GamePhase.AwaitingClaims } state) return;

            var now = clock.GetUtcNow();

            // Null until somebody calls, which is the normal state of a window: nobody is timed
            // for answering a discard. Nothing due, nothing to do.
            if (state.Pending is not { DeadlineUtc: { } due } || due > now) return;

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

            var view = GameViewBuilder.Build(
                state, room.Code, seat, seatInfo, session.RevealedSeats, HostSeat(room), session.Proposal,
                showClaimedDiscards);
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

        return GameViewBuilder.Build(
            state, room.Code, seat, SeatInfo(room, session), session.RevealedSeats, HostSeat(room), session.Proposal,
            showClaimedDiscards);
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

    /// <summary>Which seat made the table, or null if that player has since gone.</summary>
    private static int? HostSeat(Room room) =>
        room.Players.FirstOrDefault(p => p.Id == room.HostPlayerId)?.Seat;

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
    public sealed record Withdraw : GameMove;
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
