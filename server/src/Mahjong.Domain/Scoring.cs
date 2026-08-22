using System.Text.Json.Serialization;

namespace Mahjong.Domain;

/// <summary>Everything the scorer needs to know about how a hand was won.</summary>
/// <param name="ConcealedBeforeWin">
/// The tiles in hand immediately before the winning tile arrived, so the wait can be reconstructed.
/// </param>
/// <param name="Melds">Groups already on the table when the hand was won.</param>
/// <param name="WinningTile">The tile that completed the hand.</param>
/// <param name="SelfDrawn">Bunot: the winning tile came off the wall, not off a discard.</param>
/// <param name="DiscardCount">How many tiles had been discarded in the hand before this win.</param>
/// <param name="Joker">The joker face for this hand, or null when the joker rule is off.</param>
/// <param name="Bisaklat">The mano was dealt a complete hand before anyone discarded.</param>
public sealed record WinInput(
    IReadOnlyList<Tile> ConcealedBeforeWin,
    IReadOnlyList<ExposedMeld> Melds,
    Tile WinningTile,
    bool SelfDrawn,
    int DiscardCount,
    Tile? Joker = null,
    bool Bisaklat = false);

/// <summary>What one player owes or receives at the end of a hand, and why.</summary>
public sealed record Settlement(int Seat, int Delta, string Reason);

/// <summary>The full result of scoring a won hand.</summary>
/// <param name="Reading">The reading of the hand that paid the most.</param>
/// <param name="Bonuses">Each bonus that applied and what it was worth.</param>
/// <param name="BaseUnits">The plain todas value before bonuses.</param>
/// <param name="TotalUnits">Base plus bonuses, after the bunot doubling.</param>
/// <param name="Wait">The tiles the hand was waiting on before it won.</param>
public sealed record HandScore(
    Decomposition Reading,
    IReadOnlyDictionary<WinBonus, int> Bonuses,
    int BaseUnits,
    int TotalUnits,
    IReadOnlyList<Tile> Wait)
{
    [JsonIgnore]
    public int BonusUnits => Bonuses.Values.Sum();
}

/// <summary>
/// Turns a won hand into money. Pure: same inputs always produce the same breakdown, which is
/// what makes the worked examples in RULES.md testable.
/// </summary>
public static class Scorer
{
    /// <summary>
    /// Scores a won hand. A hand can usually be read more than one way, so every reading is
    /// scored and the best-paying one is returned.
    /// </summary>
    /// <exception cref="ArgumentException">The hand is not actually a winning hand.</exception>
    public static HandScore Score(WinInput input, RuleOptions? rules = null)
    {
        rules ??= RuleOptions.Default;

        var final = new List<Tile>(input.ConcealedBeforeWin) { input.WinningTile };
        var analysis = HandAnalyzer.Analyze(final, input.Melds, input.Joker, rules);

        if (!analysis.IsWin)
            throw new ArgumentException(
                $"Hand {TileNotation.Format(final)} with {input.Melds.Count} meld(s) is not a winning hand.",
                nameof(input));

        var wait = HandAnalyzer.WinningTiles(input.ConcealedBeforeWin, input.Melds, input.Joker, rules);

        HandScore? best = null;

        foreach (var reading in analysis.Readings)
        {
            var bonuses = BonusesFor(reading, input, wait, rules);
            var bonusUnits = bonuses.Values.Sum();
            var total = rules.Scoring.TodasBase + bonusUnits;

            if (input.SelfDrawn && rules.Scoring.BunotDoubles) total *= 2;

            var candidate = new HandScore(reading, bonuses, rules.Scoring.TodasBase, total, wait);
            if (best is null || candidate.TotalUnits > best.TotalUnits) best = candidate;
        }

        return best!;
    }

    /// <summary>
    /// Works out who pays what. Won off a discard, the player who fed the tile pays a multiple
    /// and the other two pay the flat total. Won off the wall (bunot), all three pay the doubled
    /// total, which <see cref="Score"/> has already applied.
    /// </summary>
    /// <param name="winnerSeat">Seat that declared todas.</param>
    /// <param name="discarderSeat">Seat that fed the winning tile, or null for a self-drawn win.</param>
    public static IReadOnlyList<Settlement> Settle(
        HandScore score,
        int winnerSeat,
        int? discarderSeat,
        RuleOptions? rules = null)
    {
        rules ??= RuleOptions.Default;

        var settlements = new List<Settlement>(4);
        var received = 0;

        for (var seat = 0; seat < 4; seat++)
        {
            if (seat == winnerSeat) continue;

            var isDiscarder = discarderSeat is { } d && d == seat;
            var owed = isDiscarder ? score.TotalUnits * rules.Scoring.DiscarderMultiplier : score.TotalUnits;

            settlements.Add(new Settlement(seat, -owed, isDiscarder ? "Fed the winning tile" : "Todas"));
            received += owed;
        }

        settlements.Insert(0, new Settlement(winnerSeat, received, "Todas"));
        return settlements;
    }

    /// <summary>
    /// An ambition is paid the moment it is declared: the player collects the same amount from
    /// each of the other three seats.
    /// </summary>
    public static IReadOnlyList<Settlement> SettleAmbition(
        Ambition ambition,
        int claimantSeat,
        RuleOptions? rules = null)
    {
        rules ??= RuleOptions.Default;

        if (!rules.Scoring.Ambitions.TryGetValue(ambition, out var units))
            throw new ArgumentException($"No value configured for ambition {ambition}.", nameof(ambition));

        var settlements = new List<Settlement>(4)
        {
            new(claimantSeat, units * 3, ambition.ToString()),
        };

        for (var seat = 0; seat < 4; seat++)
            if (seat != claimantSeat)
                settlements.Add(new Settlement(seat, -units, ambition.ToString()));

        return settlements;
    }

    private static Dictionary<WinBonus, int> BonusesFor(
        Decomposition reading,
        WinInput input,
        IReadOnlyList<Tile> wait,
        RuleOptions rules)
    {
        var bonuses = new Dictionary<WinBonus, int>();
        var profile = rules.Scoring;

        void Award(WinBonus bonus)
        {
            if (profile.Bonuses.TryGetValue(bonus, out var units) && units != 0)
                bonuses[bonus] = units;
        }

        if (input.Bisaklat)
        {
            Award(WinBonus.Bisaklat);
            return bonuses;
        }

        if (reading.IsSietePares) Award(WinBonus.SietePares);
        if (HasEscalera(reading)) Award(WinBonus.Escalera);
        if (IsFlush(reading)) Award(WinBonus.Flush);

        var bahay = reading.Bahay.ToList();
        if (bahay.Count > 0 && !reading.IsSietePares)
        {
            if (bahay.All(s => s.Kind is SetKind.Pung or SetKind.Kang)) Award(WinBonus.AllPungs);
            if (bahay.All(s => s.Kind == SetKind.Chow)) Award(WinBonus.AllChows);
        }

        // "All up": nothing was ever laid face up. A secret kang stays face down, so it does not
        // break a concealed hand.
        if (input.Melds.All(m => m.Concealed)) Award(WinBonus.Concealed);

        // "All down": every bahay is on the table and only the pair was still in hand.
        else if (input.Melds.Count == HandAnalyzer.SetsPerHand) Award(WinBonus.AllExposed);

        if (input.DiscardCount <= rules.QuickWinDiscardLimit) Award(WinBonus.QuickWin);

        switch (ClassifyWait(reading, input, wait))
        {
            case WinBonus.Paningit: Award(WinBonus.Paningit); break;
            case WinBonus.BackToBack: Award(WinBonus.BackToBack); break;
            case WinBonus.Single: Award(WinBonus.Single); break;
        }

        return bonuses;
    }

    /// <summary>
    /// Escalera is the full 1 to 9 of one suit, which in set terms means chows starting on 1, 4
    /// and 7 all in the same suit.
    /// </summary>
    private static bool HasEscalera(Decomposition reading) =>
        reading.Sets
            .Where(s => s.Kind == SetKind.Chow && !s.IsWildcard)
            .GroupBy(s => s.Suit)
            .Any(g => g.Any(s => s.LowRank == 1) && g.Any(s => s.LowRank == 4) && g.Any(s => s.LowRank == 7));

    /// <summary>
    /// Every tile in the hand from one suit. Sets made purely of jokers have no suit and are
    /// treated as compatible with whatever the rest of the hand is.
    /// </summary>
    private static bool IsFlush(Decomposition reading)
    {
        var suits = reading.Sets.Where(s => !s.IsWildcard).Select(s => s.Suit).Distinct().ToList();
        return suits.Count == 1;
    }

    /// <summary>
    /// Decides which of the mutually exclusive wait bonuses applies. Paningit outranks single,
    /// because filling the one gap in a run is the harder wait.
    /// </summary>
    private static WinBonus? ClassifyWait(Decomposition reading, WinInput input, IReadOnlyList<Tile> wait)
    {
        if (wait.Count == 1)
        {
            var completed = reading.Sets.Any(s =>
                s.Kind == SetKind.Chow
                && !s.IsWildcard
                && s.Suit == input.WinningTile.Suit
                && s.LowRank + 1 == input.WinningTile.Rank);

            return completed ? WinBonus.Paningit : WinBonus.Single;
        }

        if (wait.Count == 2 && wait.All(t => IsHeldAsAPair(input, t)))
            return WinBonus.BackToBack;

        return null;
    }

    /// <summary>
    /// Back-to-back is the wait where the hand holds two pairs and is short one tile: whichever
    /// pair gets its third tile becomes the fifth bahay, and the other stays as the eye. So the
    /// test is that the waited-on tile is already held exactly twice, not that it becomes the
    /// pair. It is the pair the player does NOT complete that ends up as the eye.
    /// </summary>
    private static bool IsHeldAsAPair(WinInput input, Tile candidate) =>
        input.ConcealedBeforeWin.Count(t => t == candidate) == 2;
}
