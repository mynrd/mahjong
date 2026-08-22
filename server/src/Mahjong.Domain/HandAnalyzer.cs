using System.Text.Json.Serialization;

namespace Mahjong.Domain;

/// <summary>The result of asking "is this hand complete, and how can it be read?"</summary>
/// <param name="Readings">
/// Every valid way of reading the hand. Empty when the hand is not a win. More than one entry is
/// normal: 1-1-1-2-3 reads as either a pung of 1s next to a floating 2-3, or a 1 next to a 1-2-3
/// chow. The scorer picks whichever reading pays most.
/// </param>
public sealed record WinAnalysis(IReadOnlyList<Decomposition> Readings)
{
    [JsonIgnore]
    public bool IsWin => Readings.Count > 0;

    public static readonly WinAnalysis NoWin = new(Array.Empty<Decomposition>());
}

/// <summary>
/// Win detection. Given the concealed tiles, the melds already on the table and the joker face
/// for this hand, works out whether the hand is complete and enumerates every way of reading it.
///
/// The search always consumes the lowest-ranked tile still left in the hand. That is what makes
/// it terminate and stay canonical: whatever group is formed at each step must include that
/// tile, because nothing later in the search will ever be able to use it.
/// </summary>
public static class HandAnalyzer
{
    /// <summary>Total groups in a winning hand, not counting the pair. Five bahay.</summary>
    public const int SetsPerHand = 5;

    /// <summary>Pairs required for a siete pares hand.</summary>
    public const int SietePairs = 7;

    /// <summary>
    /// Safety valve. A hand stuffed with jokers can technically be read a large number of ways;
    /// past this many readings the extra ones cannot change the best score.
    /// </summary>
    public const int MaxReadings = 4096;

    /// <summary>
    /// Works out whether <paramref name="concealed"/> plus <paramref name="melds"/> forms a
    /// winning hand.
    /// </summary>
    /// <param name="concealed">Tiles still in hand. Must all be playable (no bonus tiles).</param>
    /// <param name="melds">Groups already exposed on the table.</param>
    /// <param name="joker">
    /// The face chosen as this hand's joker, or null when the joker rule is off. Every tile with
    /// this face in <paramref name="concealed"/> is wild.
    /// </param>
    public static WinAnalysis Analyze(
        IReadOnlyList<Tile> concealed,
        IReadOnlyList<ExposedMeld> melds,
        Tile? joker,
        RuleOptions? rules = null)
    {
        rules ??= RuleOptions.Default;

        var counts = new int[TileSet.PlayableFaces];
        var jokers = 0;

        foreach (var tile in concealed)
        {
            if (tile.IsBonus)
                throw new ArgumentException(
                    $"Bonus tile {tile} cannot be in the concealed hand; bonus tiles are exposed on draw.",
                    nameof(concealed));

            if (joker is { } j && tile == j) jokers++;
            else counts[tile.PlayableIndex]++;
        }

        var exposedSets = melds.Count;
        var exposedAsSets = melds.Select(m => m.ToHandSet()).ToList();
        var readings = new List<Decomposition>();

        // --- Standard todas: five bahay plus one pair. ---
        var setsNeeded = SetsPerHand - exposedSets;
        if (setsNeeded >= 0 && concealed.Count == setsNeeded * 3 + 2)
        {
            var found = Search(counts, jokers, setsNeeded, pairsNeeded: 1, distinctPairs: false);
            foreach (var sets in found)
                readings.Add(new Decomposition([.. exposedAsSets, .. sets]));
        }

        // --- Siete pares: seven pairs plus one bahay. ---
        if (CanBeSietePares(melds, rules))
        {
            var sieteSetsNeeded = 1 - exposedSets;
            if (sieteSetsNeeded >= 0 && concealed.Count == SietePairs * 2 + sieteSetsNeeded * 3)
            {
                var found = Search(counts, jokers, sieteSetsNeeded, SietePairs, rules.DistinctPairsForSietePares);
                foreach (var sets in found)
                    readings.Add(new Decomposition([.. exposedAsSets, .. sets], IsSietePares: true));
            }
        }

        return readings.Count == 0
            ? WinAnalysis.NoWin
            : new WinAnalysis(Dedupe(readings));
    }

    /// <summary>
    /// Convenience overload that takes physical tiles rather than faces.
    /// </summary>
    public static WinAnalysis Analyze(
        IReadOnlyList<TileRef> concealed,
        IReadOnlyList<ExposedMeld> melds,
        Tile? joker,
        RuleOptions? rules = null)
        => Analyze(concealed.Select(t => t.Tile).ToArray(), melds, joker, rules);

    /// <summary>
    /// True if the hand is one tile away from winning, and lists the tiles that would complete it.
    /// Used both to classify the wait for scoring and to stop a player claiming a discard that
    /// does not actually finish their hand.
    /// </summary>
    public static IReadOnlyList<Tile> WinningTiles(
        IReadOnlyList<Tile> concealed,
        IReadOnlyList<ExposedMeld> melds,
        Tile? joker,
        RuleOptions? rules = null)
    {
        var winners = new List<Tile>();
        var probe = new List<Tile>(concealed.Count + 1);
        probe.AddRange(concealed);
        probe.Add(default);

        for (var index = 0; index < TileSet.PlayableFaces; index++)
        {
            var candidate = Tile.FromPlayableIndex(index);

            // A face already held four times cannot be the winning tile: no fifth copy exists.
            if (concealed.Count(t => t == candidate) + melds.Sum(m => m.Tiles.Count(t => t.Tile == candidate)) >= 4)
                continue;

            probe[^1] = candidate;
            if (Analyze(probe, melds, joker, rules).IsWin)
                winners.Add(candidate);
        }

        return winners;
    }

    private static bool CanBeSietePares(IReadOnlyList<ExposedMeld> melds, RuleOptions rules)
    {
        if (!rules.SieteParesEnabled) return false;
        if (melds.Count > 1) return false;

        // A kang would put the tile arithmetic out by one, because a kang is four tiles standing
        // in for a three-tile bahay and is paid for by the replacement draw.
        return melds.All(m => m.Kind != SetKind.Kang);
    }

    private static List<List<HandSet>> Search(
        int[] counts,
        int jokers,
        int setsNeeded,
        int pairsNeeded,
        bool distinctPairs)
    {
        var results = new List<List<HandSet>>();
        var working = (int[])counts.Clone();
        var accumulator = new List<HandSet>();

        Recurse(working, jokers, setsNeeded, pairsNeeded, accumulator, results, distinctPairs);
        return results;
    }

    private static void Recurse(
        int[] counts,
        int jokers,
        int setsLeft,
        int pairsLeft,
        List<HandSet> acc,
        List<List<HandSet>> results,
        bool distinctPairs)
    {
        if (results.Count >= MaxReadings) return;
        if (setsLeft < 0 || pairsLeft < 0) return;

        var index = FirstNonZero(counts);

        if (index < 0)
        {
            // Nothing but jokers left. They have to exactly fill what is still owed, otherwise
            // this branch is not a winning reading.
            var owed = setsLeft * 3 + pairsLeft * 2;
            if (jokers != owed) return;

            var completed = new List<HandSet>(acc);
            for (var p = 0; p < pairsLeft; p++)
                completed.Add(new HandSet(SetKind.Pair, Suit.Dots, LowRank: 0, JokersUsed: 2));
            for (var s = 0; s < setsLeft; s++)
                completed.Add(new HandSet(SetKind.Pung, Suit.Dots, LowRank: 0, JokersUsed: 3));

            results.Add(completed);
            return;
        }

        var tile = Tile.FromPlayableIndex(index);

        // --- Pair on this tile ---
        if (pairsLeft > 0 && !(distinctPairs && HasPair(acc, tile)))
        {
            for (var real = Math.Min(2, counts[index]); real >= 1; real--)
            {
                var wild = 2 - real;
                if (wild > jokers) continue;

                counts[index] -= real;
                acc.Add(new HandSet(SetKind.Pair, tile.Suit, tile.Rank, wild));
                Recurse(counts, jokers - wild, setsLeft, pairsLeft - 1, acc, results, distinctPairs);
                acc.RemoveAt(acc.Count - 1);
                counts[index] += real;
            }
        }

        if (setsLeft == 0) return;

        // --- Pung on this tile ---
        for (var real = Math.Min(3, counts[index]); real >= 1; real--)
        {
            var wild = 3 - real;
            if (wild > jokers) continue;

            counts[index] -= real;
            acc.Add(new HandSet(SetKind.Pung, tile.Suit, tile.Rank, wild));
            Recurse(counts, jokers - wild, setsLeft - 1, pairsLeft, acc, results, distinctPairs);
            acc.RemoveAt(acc.Count - 1);
            counts[index] += real;
        }

        // --- Chow starting on this tile ---
        // Winds and dragons never form a run. For the numbered suits, rank 8 and 9 cannot start
        // one, which is also what keeps a run from crossing a suit boundary in the 0-26 layout.
        if (!tile.IsSuited || tile.Rank > 7) return;

        for (var wildSecond = 0; wildSecond <= 1; wildSecond++)
        for (var wildThird = 0; wildThird <= 1; wildThird++)
        {
            var wild = wildSecond + wildThird;
            if (wild > jokers) continue;
            if (wildSecond == 0 && counts[index + 1] == 0) continue;
            if (wildThird == 0 && counts[index + 2] == 0) continue;

            counts[index]--;
            if (wildSecond == 0) counts[index + 1]--;
            if (wildThird == 0) counts[index + 2]--;

            acc.Add(new HandSet(SetKind.Chow, tile.Suit, tile.Rank, wild));
            Recurse(counts, jokers - wild, setsLeft - 1, pairsLeft, acc, results, distinctPairs);
            acc.RemoveAt(acc.Count - 1);

            counts[index]++;
            if (wildSecond == 0) counts[index + 1]++;
            if (wildThird == 0) counts[index + 2]++;
        }
    }

    private static bool HasPair(List<HandSet> acc, Tile tile)
    {
        foreach (var set in acc)
            if (set.Kind == SetKind.Pair && !set.IsWildcard && set.Suit == tile.Suit && set.LowRank == tile.Rank)
                return true;
        return false;
    }

    private static int FirstNonZero(int[] counts)
    {
        for (var i = 0; i < counts.Length; i++)
            if (counts[i] > 0)
                return i;
        return -1;
    }

    /// <summary>
    /// Different joker placements inside the same run produce the same reading, so collapse
    /// readings that describe an identical multiset of groups.
    /// </summary>
    private static List<Decomposition> Dedupe(List<Decomposition> readings)
    {
        var seen = new HashSet<string>();
        var unique = new List<Decomposition>(readings.Count);

        foreach (var reading in readings)
        {
            var key = string.Join(
                ",",
                reading.Sets
                    .Select(s => $"{s.Kind}:{s.Suit}:{s.LowRank}:{s.JokersUsed}")
                    .OrderBy(s => s, StringComparer.Ordinal));

            key = $"{reading.IsSietePares}|{key}";
            if (seen.Add(key)) unique.Add(reading);
        }

        return unique;
    }
}
