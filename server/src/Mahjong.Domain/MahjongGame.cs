using System.Diagnostics.CodeAnalysis;

namespace Mahjong.Domain;

/// <summary>Raised when a player tries something the rules do not allow.</summary>
public sealed class IllegalMoveException(string message) : InvalidOperationException(message);

/// <summary>
/// One concrete way a seat could take a discard, and the exact tiles out of its own hand that
/// doing so would cost. <see cref="Support"/> never includes the discard itself, and is empty for
/// a Todas, where the win is read off the whole hand rather than a subset of it.
/// </summary>
public sealed record ClaimCandidate(ClaimKind Kind, IReadOnlyList<TileRef> Support)
{
    /// <summary>Short label for the group this candidate would form, e.g. "Chow B3-B4-B5".</summary>
    public string Describe(TileRef discard)
    {
        if (Kind == ClaimKind.Todas) return "Todas";

        var faces = Support
            .Append(discard)
            .Select(t => t.Tile)
            .OrderBy(t => t.Suit)
            .ThenBy(t => t.Rank)
            .Select(t => t.Code);

        return Kind == ClaimKind.Chow
            ? $"Chow {string.Join("-", faces)}"
            : $"{Kind} {discard.Tile.Code}";
    }
}

/// <summary>
/// The rules engine. Every method takes the state, validates the move, mutates the state and
/// returns the events that resulted. Nothing here touches a database, a clock it did not receive,
/// or a network, which is what lets the whole ruleset be tested in-process.
/// </summary>
public static class MahjongGame
{
    public const int Seats = 4;
    public const int HandSize = 16;

    // ------------------------------------------------------------------ starting a hand

    /// <summary>
    /// Shuffles the wall, deals 16 tiles to each seat and 17 to the mano, replaces every bonus
    /// tile dealt, picks the joker and works out the opening ambitions.
    /// </summary>
    public static (GameState State, List<GameEvent> Events) Deal(
        RuleOptions rules,
        int handNumber,
        int manoSeat,
        int seed,
        DateTimeOffset now)
    {
        var rng = new Random(seed);
        var wall = TileSet.All.ToList();
        Shuffle(wall, rng);

        var state = new GameState
        {
            Rules = rules,
            HandNumber = handNumber,
            ManoSeat = manoSeat,
            Seed = seed,
            Wall = wall,
            FrontIndex = 0,
            BackIndex = TileSet.TotalTiles - 1,
            CurrentSeat = manoSeat,
        };

        var events = new List<GameEvent> { new HandDealt(handNumber, manoSeat) };

        // Deal 16 each, mano first, going counter-clockwise. Bonus tiles land in hand for now and
        // are swapped out below, which is how it is done at a real table.
        for (var round = 0; round < HandSize; round++)
            for (var offset = 0; offset < Seats; offset++)
                state.Hands[(manoSeat + offset) % Seats].Concealed.Add(state.Wall[state.FrontIndex++]);

        // The mano takes the extra tile that makes them the first to discard.
        state.Hands[manoSeat].Concealed.Add(state.Wall[state.FrontIndex++]);

        if (rules.JokerEnabled)
        {
            state.Joker = Tile.FromPlayableIndex(rng.Next(TileSet.PlayableFaces));
            events.Add(new JokerChosen(state.Joker.Value));
        }

        // Swap out dealt bonus tiles, mano first, repeating until nobody holds one.
        for (var offset = 0; offset < Seats; offset++)
        {
            var seat = (manoSeat + offset) % Seats;
            events.AddRange(ReplaceBonusTiles(state, seat));
        }

        // "No flowers": dealt none at all.
        foreach (var offset in Enumerable.Range(0, Seats))
        {
            var seat = (manoSeat + offset) % Seats;
            if (state.Hands[seat].Bonus.Count == 0)
                events.Add(PayAmbition(state, seat, Ambition.NoFlowers));
        }

        state.Phase = GamePhase.AwaitingDiscard;
        state.JustDrew = state.Hands[manoSeat].Concealed[^1];

        // "Bisaklat": the mano was dealt a hand that is already complete.
        var mano = state.Hands[manoSeat];
        if (HandAnalyzer.Analyze(mano.Concealed, mano.Melds, state.Joker, rules).IsWin)
        {
            events.Add(EndWithWin(state, manoSeat, discarderSeat: null, winningTile: state.JustDrew.Value.Tile,
                bisaklat: true));
            return (state, events);
        }

        events.Add(new TurnChanged(GamePhase.AwaitingDiscard) { Seat = manoSeat });
        return (state, events);
    }

    // ------------------------------------------------------------------ the turn cycle

    /// <summary>
    /// The current seat takes a tile from the front of the wall, exposing and replacing any bonus
    /// tiles it turns up along the way.
    /// </summary>
    public static List<GameEvent> Draw(GameState state, int seat)
    {
        Require(state.Phase == GamePhase.AwaitingDraw, $"Seat {seat} cannot draw during {state.Phase}.");
        Require(seat == state.CurrentSeat, $"It is seat {state.CurrentSeat}'s turn, not seat {seat}'s.");

        var events = new List<GameEvent>();

        if (state.WallExhausted)
        {
            events.Add(EndAsDraw(state));
            return events;
        }

        var tile = state.Wall[state.FrontIndex++];
        events.Add(new TileDrawn(tile, Replacement: false) { Seat = seat });

        if (tile.Tile.IsBonus)
        {
            state.Hands[seat].Bonus.Add(tile);
            events.Add(new BonusExposed(tile, state.Hands[seat].Bonus.Count) { Seat = seat });

            var replaced = TakeReplacement(state, seat, events);
            if (replaced is null) return events;
            tile = replaced.Value;
        }

        state.Hands[seat].Concealed.Add(tile);
        state.JustDrew = tile;
        state.Phase = GamePhase.AwaitingDiscard;
        events.Add(new TurnChanged(GamePhase.AwaitingDiscard) { Seat = seat });

        return events;
    }

    /// <summary>The current seat puts a tile down, which opens the claim window for the others.</summary>
    public static List<GameEvent> Discard(GameState state, int seat, int tileId, DateTimeOffset now)
    {
        Require(state.Phase == GamePhase.AwaitingDiscard, $"Seat {seat} cannot discard during {state.Phase}.");
        Require(seat == state.CurrentSeat, $"It is seat {state.CurrentSeat}'s turn, not seat {seat}'s.");

        var hand = state.Hands[seat];
        var index = hand.Concealed.FindIndex(t => t.Id == tileId);
        Require(index >= 0, $"Seat {seat} does not hold tile {tileId}.");

        var tile = hand.Concealed[index];
        Require(tile.Tile.IsPlayable, $"{tile} is a bonus tile and cannot be discarded.");

        hand.Concealed.RemoveAt(index);
        state.Discards.Add(new DiscardedTile(seat, tile));
        state.JustDrew = null;

        var events = new List<GameEvent> { new TileDiscarded(tile) { Seat = seat } };

        var allowed = AllowedClaims(state, tile, seat);
        if (allowed.Count == 0)
        {
            events.AddRange(AdvanceTurn(state));
            return events;
        }

        state.Phase = GamePhase.AwaitingClaims;
        state.Pending = new PendingClaim
        {
            Tile = tile,
            FromSeat = seat,
            DeadlineUtc = now.AddSeconds(state.Rules.ClaimWindowSeconds),
        };

        // Seats with nothing to claim are treated as having passed already, so the window closes
        // as soon as the seats that actually have a decision have made it.
        for (var other = 0; other < Seats; other++)
            if (other != seat && !allowed.ContainsKey(other))
                state.Pending.Passed.Add(other);

        events.Add(new ClaimWindowOpened(tile, seat, state.Pending.DeadlineUtc, allowed) { Seat = seat });
        return events;
    }

    /// <summary>A seat says it does not want the discard.</summary>
    public static List<GameEvent> Pass(GameState state, int seat, DateTimeOffset now)
    {
        Require(state.Phase == GamePhase.AwaitingClaims && state.Pending is not null,
            $"Seat {seat} cannot pass during {state.Phase}.");
        Require(seat != state.Pending!.FromSeat, "The discarding seat has nothing to pass on.");

        state.Pending.Declared.Remove(seat);
        state.Pending.Passed.Add(seat);

        return TryCloseClaimWindow(state, now, forced: false);
    }

    /// <summary>A seat declares a claim on the discard. The window still has to close before it applies.</summary>
    public static List<GameEvent> Claim(GameState state, int seat, ClaimKind kind, IReadOnlyList<int> tileIds,
        DateTimeOffset now)
    {
        Require(state.Phase == GamePhase.AwaitingClaims && state.Pending is not null,
            $"Seat {seat} cannot claim during {state.Phase}.");

        var pending = state.Pending!;
        Require(seat != pending.FromSeat, "A seat cannot claim its own discard.");

        var candidates = ClaimCandidates(state, pending.Tile, pending.FromSeat, seat);
        Require(candidates.Any(c => c.Kind == kind),
            $"Seat {seat} cannot declare {kind} on {pending.Tile}.");

        // No tiles named means "you pick" - what bots send and what the client sent before it
        // could choose. Named tiles have to match one candidate exactly, because the player must
        // get the group they picked rather than the one the server would have re-derived.
        IReadOnlyList<int> declaredTiles = [];

        if (tileIds.Count > 0)
        {
            Require(tileIds.Distinct().Count() == tileIds.Count, "The same tile cannot be picked twice.");

            var supporting = ResolveHeld(state.Hands[seat], tileIds);
            var picked = supporting.Select(t => t.Tile.Code).Order().ToList();

            // Matched on faces rather than ids: holding three 5-bamboo, which two the player
            // happened to tap for a pung is not a decision the rules have an opinion about.
            Require(
                candidates.Any(c => c.Kind == kind
                    && c.Support.Select(t => t.Tile.Code).Order().SequenceEqual(picked)),
                $"Seat {seat}'s tiles do not make a {kind} with {pending.Tile}.");

            declaredTiles = supporting.Select(t => t.Id).ToList();
        }

        pending.Passed.Remove(seat);
        pending.Declared[seat] = new DeclaredClaim(kind, declaredTiles);

        return TryCloseClaimWindow(state, now, forced: false);
    }

    /// <summary>
    /// Closes the claim window because the deadline passed. Seats that never answered are read as
    /// having passed.
    /// </summary>
    public static List<GameEvent> ExpireClaimWindow(GameState state, DateTimeOffset now)
    {
        if (state.Phase != GamePhase.AwaitingClaims || state.Pending is null) return [];
        return TryCloseClaimWindow(state, now, forced: true);
    }

    // ------------------------------------------------------------------ declarations on your own turn

    /// <summary>Four self-drawn copies, declared face down. Pays more than an open kang.</summary>
    public static List<GameEvent> DeclareSecretKang(GameState state, int seat, Tile face)
    {
        Require(state.Phase == GamePhase.AwaitingDiscard && seat == state.CurrentSeat,
            $"Seat {seat} cannot declare a secret kang during {state.Phase}.");

        var hand = state.Hands[seat];
        var copies = hand.Concealed.Where(t => t.Tile == face).Take(4).ToList();
        Require(copies.Count == 4, $"Seat {seat} does not hold four {face}.");

        foreach (var tile in copies) hand.Concealed.Remove(tile);
        var meld = new ExposedMeld(SetKind.Kang, copies, Concealed: true);
        hand.Melds.Add(meld);

        var events = new List<GameEvent> { new MeldFormed(meld) { Seat = seat } };
        events.Add(PayAmbition(state, seat, Ambition.SecretKang));

        var replacement = TakeReplacement(state, seat, events);
        if (replacement is null) return events;

        hand.Concealed.Add(replacement.Value);
        state.JustDrew = replacement.Value;
        events.Add(new TurnChanged(GamePhase.AwaitingDiscard) { Seat = seat });

        return events;
    }

    /// <summary>Extends an already-exposed pung with the self-drawn fourth tile.</summary>
    public static List<GameEvent> DeclareSagasa(GameState state, int seat, Tile face)
    {
        Require(state.Phase == GamePhase.AwaitingDiscard && seat == state.CurrentSeat,
            $"Seat {seat} cannot declare sagasa during {state.Phase}.");

        var hand = state.Hands[seat];
        var pungIndex = hand.Melds.FindIndex(m => m.Kind == SetKind.Pung && m.BaseTile == face);
        Require(pungIndex >= 0, $"Seat {seat} has no exposed pung of {face} to extend.");

        var fourth = hand.Concealed.FirstOrDefault(t => t.Tile == face, new TileRef(-1));
        Require(fourth.Id >= 0, $"Seat {seat} does not hold a fourth {face}.");

        hand.Concealed.Remove(fourth);

        var pung = hand.Melds[pungIndex];
        var kang = pung with
        {
            Kind = SetKind.Kang,
            Tiles = [.. pung.Tiles, fourth],
            FromSagasa = true,
        };
        hand.Melds[pungIndex] = kang;

        var events = new List<GameEvent> { new MeldFormed(kang) { Seat = seat } };
        events.Add(PayAmbition(state, seat, Ambition.Sagasa));

        var replacement = TakeReplacement(state, seat, events);
        if (replacement is null) return events;

        hand.Concealed.Add(replacement.Value);
        state.JustDrew = replacement.Value;
        events.Add(new TurnChanged(GamePhase.AwaitingDiscard) { Seat = seat });

        return events;
    }

    /// <summary>Declares a complete hand on the tile just drawn. This is a bunot, a self-drawn win.</summary>
    public static List<GameEvent> DeclareTodasOnDraw(GameState state, int seat)
    {
        Require(state.Phase == GamePhase.AwaitingDiscard && seat == state.CurrentSeat,
            $"Seat {seat} cannot declare todas during {state.Phase}.");
        Require(state.JustDrew is not null, "There is no drawn tile to win on.");

        var hand = state.Hands[seat];
        Require(HandAnalyzer.Analyze(hand.Concealed, hand.Melds, state.Joker, state.Rules).IsWin,
            $"Seat {seat}'s hand is not complete.");

        return [EndWithWin(state, seat, discarderSeat: null, winningTile: state.JustDrew.Value.Tile, bisaklat: false)];
    }

    // ------------------------------------------------------------------ what can be claimed

    /// <summary>
    /// Works out, for each seat other than the discarder, which claims the rules permit on this
    /// tile. Used both to open the claim window and to validate a claim when it arrives.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<ClaimKind>> AllowedClaims(
        GameState state, TileRef discard, int fromSeat)
    {
        var result = new Dictionary<int, IReadOnlyList<ClaimKind>>();

        for (var seat = 0; seat < Seats; seat++)
        {
            if (seat == fromSeat) continue;

            // Derived from the candidates rather than checked separately, so the kinds offered and
            // the concrete groups behind them can never disagree.
            var kinds = ClaimCandidates(state, discard, fromSeat, seat)
                .Select(c => c.Kind)
                .Distinct()
                .ToList();

            if (kinds.Count > 0) result[seat] = kinds;
        }

        return result;
    }

    /// <summary>
    /// Every concrete way one seat could take this discard, with the exact tiles from its own hand
    /// each way would cost. Highest-ranked kind first, which is the order
    /// <see cref="PickWinningClaim"/> would resolve a contested tile in.
    ///
    /// A joker is not wild here: the checks compare literal faces, the same as the rest of the
    /// claim rules. Offering a joker-backed claim would only get it refused a moment later.
    /// </summary>
    public static IReadOnlyList<ClaimCandidate> ClaimCandidates(
        GameState state, TileRef discard, int fromSeat, int seat)
    {
        if (seat == fromSeat || seat < 0 || seat >= Seats) return [];

        var hand = state.Hands[seat];
        var face = discard.Tile;
        var candidates = new List<ClaimCandidate>();

        // The win is read off the whole hand, so there is no subset of tiles to pick.
        if (CanWinOn(state, seat, face)) candidates.Add(new ClaimCandidate(ClaimKind.Todas, []));

        var copies = hand.Concealed.Where(t => t.Tile == face).ToList();

        if (copies.Count >= 3) candidates.Add(new ClaimCandidate(ClaimKind.Kang, copies.Take(3).ToList()));

        // Which two physical copies is not a player decision - the faces are identical.
        if (copies.Count >= 2) candidates.Add(new ClaimCandidate(ClaimKind.Pung, copies.Take(2).ToList()));

        var chowAllowed = face.IsSuited
            && (!state.Rules.ChowFromLeftOnly || GameState.IsLeftOf(seat, fromSeat));

        if (chowAllowed)
            foreach (var run in RunPartners(hand, face))
                candidates.Add(new ClaimCandidate(ClaimKind.Chow, run));

        return candidates;
    }

    private static bool CanWinOn(GameState state, int seat, Tile face)
    {
        var hand = state.Hands[seat];
        var probe = hand.ConcealedFaces.Append(face).ToList();

        if (!HandAnalyzer.Analyze(probe, hand.Melds, state.Joker, state.Rules).IsWin) return false;

        // Some tables refuse to let a joker stand in for the tile you claim off a discard. Under
        // that rule the hand has to be complete without leaning on a joker for the winning tile,
        // which is the same as checking it still wins with the joker rule turned off for the check.
        if (!state.Rules.JokerCanCompleteClaimedWin && state.Joker is not null)
            return HandAnalyzer.Analyze(probe, hand.Melds, joker: null, state.Rules).IsWin;

        return true;
    }

    /// <summary>
    /// Every distinct pair of held tiles that makes a run with <paramref name="face"/>: the claimed
    /// tile at the top of the run, in the middle, or at the bottom. Lowest run first, so a caller
    /// that only wants one gets the same one the old auto-pick chose.
    /// </summary>
    private static List<List<TileRef>> RunPartners(PlayerHand hand, Tile face)
    {
        var runs = new List<List<TileRef>>();
        if (!face.IsSuited) return runs;

        TileRef? Find(int rank)
        {
            if (rank is < 1 or > 9) return null;
            var wanted = new Tile(face.Suit, rank);
            var match = hand.Concealed.FirstOrDefault(t => t.Tile == wanted, new TileRef(-1));
            return match.Id >= 0 ? match : null;
        }

        foreach (var low in new[] { face.Rank - 2, face.Rank - 1, face.Rank })
        {
            if (low < 1 || low + 2 > 9) continue;

            var wanted = new[] { low, low + 1, low + 2 }.Where(r => r != face.Rank).ToList();
            if (wanted.Count != 2) continue;

            var first = Find(wanted[0]);
            var second = Find(wanted[1]);
            if (first is null || second is null) continue;

            runs.Add([first.Value, second.Value]);
        }

        return runs;
    }

    // ------------------------------------------------------------------ closing the claim window

    private static List<GameEvent> TryCloseClaimWindow(GameState state, DateTimeOffset now, bool forced)
    {
        var pending = state.Pending!;
        var answered = pending.Declared.Count + pending.Passed.Count;

        // Three other seats have to be accounted for before the tile can move on.
        if (!forced && answered < Seats - 1) return [];

        if (pending.Declared.Count == 0)
        {
            state.Pending = null;
            var events = new List<GameEvent> { new ClaimWindowClosed(pending.Tile) };
            events.AddRange(AdvanceTurn(state));
            return events;
        }

        var winner = PickWinningClaim(state, pending);
        return ApplyClaim(state, winner.Seat, winner.Claim, pending);
    }

    /// <summary>
    /// Resolves competing claims. A declared win outranks a pung or kang, which outrank a chow.
    /// Two seats claiming the same rank are separated by who sits nearer after the discarder.
    /// </summary>
    private static (int Seat, DeclaredClaim Claim) PickWinningClaim(GameState state, PendingClaim pending)
    {
        int Rank(ClaimKind kind) => kind switch
        {
            ClaimKind.Todas => state.Rules.TodasBeatsPungAndKang ? 3 : 1,
            ClaimKind.Kang => 2,
            ClaimKind.Pung => 2,
            ClaimKind.Chow => 0,
            _ => -1,
        };

        int Distance(int seat) => (seat - pending.FromSeat + Seats) % Seats;

        return pending.Declared
            .OrderByDescending(kv => Rank(kv.Value.Kind))
            .ThenBy(kv => Distance(kv.Key))
            .Select(kv => (Seat: kv.Key, Claim: kv.Value))
            .First();
    }

    private static List<GameEvent> ApplyClaim(GameState state, int seat, DeclaredClaim declared, PendingClaim pending)
    {
        var kind = declared.Kind;
        var hand = state.Hands[seat];
        var face = pending.Tile.Tile;
        var events = new List<GameEvent>();

        // The tile leaves the discard pile whichever way it is claimed.
        var lastIndex = state.Discards.Count - 1;
        state.Discards[lastIndex] = state.Discards[lastIndex] with { Claimed = true };
        state.Pending = null;

        if (kind == ClaimKind.Todas)
        {
            events.Add(EndWithWin(state, seat, pending.FromSeat, pending.Tile.Tile, bisaklat: false));
            return events;
        }

        var needed = kind switch
        {
            ClaimKind.Kang => 3,
            ClaimKind.Pung => 2,
            ClaimKind.Chow => 2,
            _ => throw new IllegalMoveException($"Cannot apply claim {kind}."),
        };

        List<TileRef> supporting;

        if (declared.TileIds.Count > 0)
        {
            // The player named these tiles when the window was open. Re-checked here rather than
            // trusted, because a sagasa or a second claim can have moved the hand on since.
            supporting = ResolveHeld(hand, declared.TileIds);

            if (supporting.Count != needed)
                throw new IllegalMoveException(
                    $"Seat {seat} named {supporting.Count} tiles for a {kind}, which needs {needed}.");
        }
        else if (kind == ClaimKind.Chow)
        {
            supporting = RunPartners(hand, face).FirstOrDefault()
                ?? throw new IllegalMoveException($"Seat {seat} can no longer form a run with {face}.");
        }
        else
        {
            supporting = hand.Concealed.Where(t => t.Tile == face).Take(needed).ToList();
            if (supporting.Count != needed)
                throw new IllegalMoveException($"Seat {seat} no longer holds {needed} copies of {face}.");
        }

        foreach (var tile in supporting) hand.Concealed.Remove(tile);

        var meld = new ExposedMeld(
            kind == ClaimKind.Chow ? SetKind.Chow : kind == ClaimKind.Kang ? SetKind.Kang : SetKind.Pung,
            [.. supporting, pending.Tile],
            Concealed: false,
            ClaimedFromSeat: pending.FromSeat);

        hand.Melds.Add(meld);
        events.Add(new MeldFormed(meld) { Seat = seat });

        state.CurrentSeat = seat;

        if (kind == ClaimKind.Kang)
        {
            events.Add(PayAmbition(state, seat, Ambition.Kang));

            var replacement = TakeReplacement(state, seat, events);
            if (replacement is null) return events;

            hand.Concealed.Add(replacement.Value);
            state.JustDrew = replacement.Value;
        }
        else
        {
            state.JustDrew = null;
        }

        // A claim skips whoever sat between the discarder and the claimant. The claimant is now
        // holding one tile too many and has to discard.
        state.Phase = GamePhase.AwaitingDiscard;
        events.Add(new TurnChanged(GamePhase.AwaitingDiscard) { Seat = seat });

        return events;
    }

    // ------------------------------------------------------------------ shared mechanics

    private static List<GameEvent> AdvanceTurn(GameState state)
    {
        var events = new List<GameEvent>();

        state.CurrentSeat = GameState.NextSeat(state.CurrentSeat);
        state.JustDrew = null;

        if (state.WallExhausted)
        {
            events.Add(EndAsDraw(state));
            return events;
        }

        state.Phase = GamePhase.AwaitingDraw;
        events.Add(new TurnChanged(GamePhase.AwaitingDraw) { Seat = state.CurrentSeat });
        return events;
    }

    /// <summary>
    /// Turns over bonus tiles a seat is holding and takes replacements from the tail of the wall,
    /// repeating because a replacement can itself be a bonus tile.
    /// </summary>
    private static List<GameEvent> ReplaceBonusTiles(GameState state, int seat)
    {
        var events = new List<GameEvent>();
        var hand = state.Hands[seat];

        while (true)
        {
            var index = hand.Concealed.FindIndex(t => t.Tile.IsBonus);
            if (index < 0) break;

            var bonus = hand.Concealed[index];
            hand.Concealed.RemoveAt(index);
            hand.Bonus.Add(bonus);
            events.Add(new BonusExposed(bonus, hand.Bonus.Count) { Seat = seat });

            var replacement = TakeReplacement(state, seat, events);
            if (replacement is null) break;

            hand.Concealed.Add(replacement.Value);
        }

        return events;
    }

    /// <summary>
    /// Takes one tile from the tail of the wall. Bonus tiles turned up this way are exposed and
    /// the draw repeats. Returns null when the wall ran out, in which case the hand has ended.
    /// </summary>
    private static TileRef? TakeReplacement(GameState state, int seat, List<GameEvent> events)
    {
        while (true)
        {
            if (state.WallExhausted)
            {
                events.Add(EndAsDraw(state));
                return null;
            }

            var tile = state.Wall[state.BackIndex--];
            events.Add(new TileDrawn(tile, Replacement: true) { Seat = seat });

            if (!tile.Tile.IsBonus) return tile;

            var hand = state.Hands[seat];
            hand.Bonus.Add(tile);
            events.Add(new BonusExposed(tile, hand.Bonus.Count) { Seat = seat });
        }
    }

    private static AmbitionEarned PayAmbition(GameState state, int seat, Ambition ambition) =>
        new(ambition, Scorer.SettleAmbition(ambition, seat, state.Rules)) { Seat = seat };

    private static HandEnded EndWithWin(
        GameState state, int winnerSeat, int? discarderSeat, Tile winningTile, bool bisaklat)
    {
        var hand = state.Hands[winnerSeat];

        // The scorer wants the hand as it stood before the winning tile arrived, so the wait can
        // be reconstructed. How to get there depends on where the tile came from: a self-drawn
        // tile is already sitting in the hand and has to be taken back out, whereas a tile claimed
        // off a discard was never added, so the hand is already in the right state. Removing a
        // copy in the claimed case would silently delete a tile the player really holds.
        var before = hand.ConcealedFaces.ToList();

        if (discarderSeat is null)
        {
            var index = before.IndexOf(winningTile);
            if (index >= 0) before.RemoveAt(index);
        }

        var input = new WinInput(
            before,
            hand.Melds,
            winningTile,
            SelfDrawn: discarderSeat is null,
            DiscardCount: state.Discards.Count(d => !d.Claimed),
            Joker: state.Joker,
            Bisaklat: bisaklat);

        var score = Scorer.Score(input, state.Rules);
        var settlements = Scorer.Settle(score, winnerSeat, discarderSeat, state.Rules);

        var outcome = new HandOutcome(
            bisaklat ? HandEndReason.Bisaklat : HandEndReason.Todas,
            winnerSeat,
            score,
            settlements);

        state.Outcome = outcome;
        state.Phase = GamePhase.HandOver;
        state.Pending = null;

        return new HandEnded(outcome) { Seat = winnerSeat };
    }

    private static HandEnded EndAsDraw(GameState state)
    {
        var outcome = new HandOutcome(HandEndReason.WallExhausted, null, null, []);
        state.Outcome = outcome;
        state.Phase = GamePhase.HandOver;
        state.Pending = null;

        return new HandEnded(outcome);
    }

    private static List<TileRef> ResolveHeld(PlayerHand hand, IReadOnlyList<int> tileIds)
    {
        var resolved = new List<TileRef>(tileIds.Count);

        foreach (var id in tileIds)
        {
            var match = hand.Concealed.FirstOrDefault(t => t.Id == id, new TileRef(-1));
            if (match.Id < 0) throw new IllegalMoveException($"Tile {id} is not in hand.");
            resolved.Add(match);
        }

        return resolved;
    }

    private static void Shuffle<T>(IList<T> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Guard for every rule check. The attribute tells the compiler that a false condition never
    /// returns, so nullable analysis trusts the checks that precede a dereference.
    /// </summary>
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new IllegalMoveException(message);
    }
}
