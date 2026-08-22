using Mahjong.Domain;

namespace Mahjong.Api;

/// <summary>
/// A deliberately plain bot. Its job is to fill an empty seat so a table of two or three people
/// can still play, and to let an automated test drive a whole hand without four browsers. It is
/// not trying to play well.
///
/// The policy: take a win if there is one, claim a discard whenever the rules allow a group it is
/// willing to take, and otherwise throw away the tile that is doing the least for the hand.
/// </summary>
public static class SimpleBot
{
    /// <summary>Decides the bot's next move, or null if it has nothing to do right now.</summary>
    public static GameMove? Decide(GameState state, int seat) => state.Phase switch
    {
        GamePhase.AwaitingDraw when state.CurrentSeat == seat => new GameMove.Draw(),
        GamePhase.AwaitingDiscard when state.CurrentSeat == seat => DecideOnTurn(state, seat),
        GamePhase.AwaitingClaims when CanStillAnswer(state, seat) => DecideOnClaim(state, seat),
        _ => null,
    };

    private static GameMove DecideOnTurn(GameState state, int seat)
    {
        var hand = state.Hands[seat];

        if (state.JustDrew is not null
            && HandAnalyzer.Analyze(hand.Concealed, hand.Melds, state.Joker, state.Rules).IsWin)
            return new GameMove.Todas();

        return new GameMove.Discard(ChooseDiscard(state, seat).Id);
    }

    /// <summary>
    /// Answers an open claim window. Candidates come back strongest kind first, which is the same
    /// order the engine resolves a contested tile in, so the first one the bot is willing to take
    /// is the one to declare. The tiles are left unnamed: the engine then picks which physical
    /// copies out of the hand the group is built from.
    /// </summary>
    private static GameMove DecideOnClaim(GameState state, int seat)
    {
        var pending = state.Pending!;
        var candidates = MahjongGame.ClaimCandidates(state, pending.Tile, pending.FromSeat, seat);

        foreach (var candidate in candidates)
            if (IsWorthTaking(state, seat, candidate))
                return new GameMove.Claim(candidate.Kind, []);

        return new GameMove.Pass();
    }

    /// <summary>
    /// Whether a legal claim is one the bot actually wants. Todas always is: the hand is over and
    /// it won. Everything else costs tiles out of hand, so two of those trades are refused.
    /// </summary>
    private static bool IsWorthTaking(GameState state, int seat, ClaimCandidate candidate)
    {
        if (candidate.Kind == ClaimKind.Todas) return true;

        // A joker stands in for any face, so spending one on the face it literally shows throws
        // away the flexibility that made it worth holding.
        if (state.Joker is { } joker && candidate.Support.Any(t => t.Tile == joker)) return false;

        // A chow is the weakest claim and costs two tiles. One that spends a tile the hand holds
        // twice breaks a pair - a group that was already half built - to finish a run, which
        // leaves the hand with fewer ways to close than it started with.
        if (candidate.Kind == ClaimKind.Chow)
            return !candidate.Support.Any(t => state.Hands[seat].Has(t.Tile, atLeast: 2));

        return true;
    }

    private static bool CanStillAnswer(GameState state, int seat)
    {
        if (state.Pending is not { } pending) return false;
        if (pending.FromSeat == seat) return false;

        return !pending.Passed.Contains(seat) && !pending.Declared.ContainsKey(seat);
    }

    /// <summary>
    /// Picks the least connected tile. A tile scores for every copy of itself the hand holds and
    /// for every near neighbour in the same suit, because both are ways it could still become part
    /// of a group. The lowest score gets thrown, with terminals broken in favour of discarding,
    /// since a 1 or a 9 can only sit at one end of a run.
    /// </summary>
    public static TileRef ChooseDiscard(GameState state, int seat)
    {
        var hand = state.Hands[seat];
        var counts = new int[TileSet.PlayableFaces];

        foreach (var tile in hand.Concealed)
            if (tile.Tile.IsPlayable)
                counts[tile.Tile.PlayableIndex]++;

        TileRef best = default;
        var bestScore = int.MaxValue;

        foreach (var tile in hand.Concealed)
        {
            // Never throw the joker away: it can stand in for anything.
            if (state.Joker is { } joker && tile.Tile == joker) continue;

            var score = Score(tile.Tile, counts);
            if (score >= bestScore) continue;

            bestScore = score;
            best = tile;
        }

        // Every tile was a joker, which can happen only in a freak hand. Throw the first one.
        return bestScore == int.MaxValue ? hand.Concealed[0] : best;
    }

    private static int Score(Tile tile, int[] counts)
    {
        var index = tile.PlayableIndex;

        // Copies of the same face are worth most: two is a pair, three is a bahay outright.
        var score = counts[index] * 3;

        // Winds and dragons can only ever be collected into a pung, so copies are all they score.
        // Walking the neighbouring indexes for them would read across the wind/dragon boundary.
        if (!tile.IsSuited) return score;

        for (var offset = 1; offset <= 2; offset++)
        {
            if (tile.Rank - offset >= 1) score += counts[index - offset] * (3 - offset);
            if (tile.Rank + offset <= 9) score += counts[index + offset] * (3 - offset);
        }

        // A terminal has half the run possibilities of a middle tile, so break ties by throwing it.
        if (tile.IsTerminal) score -= 1;

        return score;
    }
}
