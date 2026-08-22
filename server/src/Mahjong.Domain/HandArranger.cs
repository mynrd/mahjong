namespace Mahjong.Domain;

/// <summary>
/// What one block of a hand is doing. The order of the members is the order the blocks are laid
/// out in: complete sets first, then the pair, then the two-tile partials, then the leftovers.
/// </summary>
public enum HandGroupKind
{
    /// <summary>Four of a face.</summary>
    Kang,

    /// <summary>Three of a face.</summary>
    Pung,

    /// <summary>Three in a run, same suit.</summary>
    Chow,

    /// <summary>Two of a face, standing as the hand's eye.</summary>
    Pair,

    /// <summary>Two tiles one away from a set: two adjacent ranks, or a one-gap run.</summary>
    Partial,

    /// <summary>A tile doing nothing with anything else in the hand.</summary>
    Floater,
}

/// <summary>One block of a hand, as Auto Arrange lays it out.</summary>
/// <param name="Tiles">
/// The physical tiles, in reading order. A joker standing in for a tile sits in the position it
/// stands in for, so a B3-joker-B5 chow reads left to right as the run it represents.
/// </param>
/// <param name="Needs">Faces that would complete this group. Empty for a complete set, a pair or a floater.</param>
/// <param name="JokersUsed">How many jokers this group spent. Zero for a joker left over as a floater.</param>
public sealed record HandGroup(
    HandGroupKind Kind,
    IReadOnlyList<TileRef> Tiles,
    IReadOnlyList<Tile> Needs,
    int JokersUsed);

/// <summary>
/// Reads a hand as blocks - which tiles are already a set, which are a pair, which two are one
/// tile short - so the client can lay the hand out by what the tiles are doing rather than by suit.
///
/// This is display only. Nothing here decides a move, and the caller never sends the result back.
///
/// The search has the same shape as <see cref="HandAnalyzer"/>: always consume the lowest-ranked
/// tile still left, so whatever group is formed at each step must include that tile. That is what
/// keeps it terminating and canonical. The difference is that it has no exact quota to fill, so
/// every leaf is a valid arrangement and the best-scoring one wins.
/// </summary>
public static class HandArranger
{
    /// <summary>
    /// Safety valve, same idea as <see cref="HandAnalyzer.MaxReadings"/>: past this many nodes the
    /// best arrangement found so far is returned.
    /// </summary>
    public const int MaxNodes = 60_000;

    /// <summary>Blocks past this many can never be used by a winning hand, so they do not score.</summary>
    private const int UsefulGroups = HandAnalyzer.SetsPerHand;

    /// <param name="concealed">The tiles still in hand. Must all be playable.</param>
    /// <param name="melds">Groups already exposed. Not returned, but they count toward the five sets.</param>
    /// <param name="joker">The wild face for this hand, or null when the joker rule is off.</param>
    public static IReadOnlyList<HandGroup> Arrange(
        IReadOnlyList<TileRef> concealed,
        IReadOnlyList<ExposedMeld> melds,
        Tile? joker,
        RuleOptions? rules = null)
    {
        rules ??= RuleOptions.Default;
        return new Searcher(concealed, melds?.Count ?? 0, rules.JokerEnabled ? joker : null).Run();
    }

    /// <summary>
    /// The hand laid out as the reading the scorer actually paid on, rather than as the best
    /// arrangement of tiles that might still be going somewhere. This is what a finished hand
    /// wants: the question on the last frame is "why did that win", and the answer is the five
    /// bahay and the pair the money was counted from.
    ///
    /// <see cref="Decomposition.Sets"/> is face-level - a joker inside a set has its own unrelated
    /// face - so each set is walked face by face and matched against the physical tiles still in
    /// hand, a joker filling any face the hand is short of. As in <see cref="HandGroup.Tiles"/>,
    /// the joker sits in the slot it stands in for, so a B1-joker-B3 chow reads as the run it is.
    ///
    /// Returns an empty list if the reading does not account for exactly these tiles, which lets
    /// the caller fall back to <see cref="Arrange"/> rather than draw a hand that is missing tiles.
    /// </summary>
    /// <param name="reading">The winning reading, exposed melds first, as <see cref="HandAnalyzer"/> builds it.</param>
    /// <param name="concealed">The tiles still in hand, including the winning tile.</param>
    /// <param name="melds">Groups already exposed. Not laid out, only counted off the front of the reading.</param>
    /// <param name="joker">The wild face for this hand, or null when the joker rule is off.</param>
    public static IReadOnlyList<HandGroup> FromReading(
        Decomposition reading,
        IReadOnlyList<TileRef> concealed,
        IReadOnlyList<ExposedMeld> melds,
        Tile? joker)
    {
        var real = new List<TileRef>();
        var jokers = new List<TileRef>();

        foreach (var tile in concealed)
        {
            if (joker is { } face && tile.Tile == face) jokers.Add(tile);
            else real.Add(tile);
        }

        // Every reading carries the exposed melds at the front, so the tail is what is still held.
        var sets = reading.Sets.Skip(melds?.Count ?? 0).ToList();
        if (sets.Count == 0) return [];

        var used = new bool[real.Count];
        var spent = 0;
        var built = new List<(HandSet Set, HandGroup Group)>(sets.Count);

        // A set made purely of jokers commits to no suit or rank, so it is filled last: taking its
        // jokers first could starve a set that has a real face to cover.
        foreach (var set in sets.OrderBy(s => s.IsWildcard ? 1 : 0))
        {
            var tiles = new List<TileRef>(4);
            var wild = 0;

            foreach (var wanted in set.IsWildcard ? WildcardFaces(set.Kind) : set.Faces.Select(f => (Tile?)f))
            {
                var index = wanted is { } needed ? FindFree(real, used, needed) : -1;

                if (index >= 0)
                {
                    used[index] = true;
                    tiles.Add(real[index]);
                    continue;
                }

                if (spent >= jokers.Count) return [];

                tiles.Add(jokers[spent++]);
                wild++;
            }

            built.Add((set, new HandGroup(KindOf(set.Kind), tiles, [], wild)));
        }

        // A reading that leaves a tile over, or wants a joker the hand does not hold, is not a
        // reading of this hand. Drawing it would silently drop tiles off the screen.
        if (Array.IndexOf(used, false) >= 0 || spent != jokers.Count) return [];

        // Same layout order as the search: kind, then suit, then rank, so a hand does not jump
        // about when the replay steps onto the frame that ends it.
        built.Sort((a, b) =>
        {
            var byKind = a.Group.Kind.CompareTo(b.Group.Kind);
            if (byKind != 0) return byKind;

            var bySuit = a.Set.Suit.CompareTo(b.Set.Suit);
            if (bySuit != 0) return bySuit;

            var byRank = a.Set.LowRank.CompareTo(b.Set.LowRank);
            return byRank != 0 ? byRank : a.Group.Tiles[0].Id.CompareTo(b.Group.Tiles[0].Id);
        });

        return built.Select(b => b.Group).ToList();
    }

    /// <summary>One null per slot of a wildcard set: nothing to match, so every slot takes a joker.</summary>
    private static IEnumerable<Tile?> WildcardFaces(SetKind kind) =>
        Enumerable.Repeat((Tile?)null, kind switch
        {
            SetKind.Pair => 2,
            SetKind.Kang => 4,
            _ => 3,
        });

    private static int FindFree(List<TileRef> tiles, bool[] used, Tile face)
    {
        for (var i = 0; i < tiles.Count; i++)
            if (!used[i] && tiles[i].Tile == face) return i;
        return -1;
    }

    private static HandGroupKind KindOf(SetKind kind) => kind switch
    {
        SetKind.Kang => HandGroupKind.Kang,
        SetKind.Pung => HandGroupKind.Pung,
        SetKind.Chow => HandGroupKind.Chow,
        SetKind.Pair => HandGroupKind.Pair,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// A group while the search is still working, carrying the suit and rank it is built on. That
    /// cannot be read back off <see cref="HandGroup.Tiles"/>, because a joker inside a group has
    /// its own unrelated face.
    /// </summary>
    private sealed record Block(
        HandGroupKind Kind,
        Suit Suit,
        int LowRank,
        List<TileRef> Tiles,
        IReadOnlyList<Tile> Needs,
        int JokersUsed);

    /// <summary>
    /// How good an arrangement is, highest first. Section 6.4 of the plan spells this out: sets,
    /// then a pair, then partials that could still be used, then jokers left unspent.
    /// </summary>
    private readonly record struct Score(int Sets, int HasPair, int Partials, int JokersUnspent)
        : IComparable<Score>
    {
        public int CompareTo(Score other)
        {
            var bySets = Sets.CompareTo(other.Sets);
            if (bySets != 0) return bySets;

            var byPair = HasPair.CompareTo(other.HasPair);
            if (byPair != 0) return byPair;

            var byPartials = Partials.CompareTo(other.Partials);
            if (byPartials != 0) return byPartials;

            return JokersUnspent.CompareTo(other.JokersUnspent);
        }
    }

    private sealed class Searcher
    {
        private readonly List<TileRef> _tiles;
        private readonly bool[] _used;
        private readonly List<TileRef> _jokers;
        private readonly int _exposedSets;
        private readonly List<Block> _acc = [];

        private int _jokersSpent;
        private int _nodes;

        private List<Block>? _best;
        private Score _bestScore;

        public Searcher(IReadOnlyList<TileRef> concealed, int exposedSets, Tile? joker)
        {
            _exposedSets = exposedSets;
            _jokers = [];
            _tiles = [];

            foreach (var tile in concealed)
            {
                if (tile.Tile.IsBonus)
                    throw new ArgumentException(
                        $"Bonus tile {tile} cannot be in the concealed hand; bonus tiles are exposed on draw.",
                        nameof(concealed));

                if (joker is { } j && tile.Tile == j) _jokers.Add(tile);
                else _tiles.Add(tile);
            }

            // Sorting is what makes "always take the lowest" mean something, and it is also what
            // puts copies of the same face next to each other so they can be counted in one pass.
            _tiles.Sort((a, b) =>
            {
                var bySuit = a.Tile.Suit.CompareTo(b.Tile.Suit);
                if (bySuit != 0) return bySuit;

                var byRank = a.Tile.Rank.CompareTo(b.Tile.Rank);
                return byRank != 0 ? byRank : a.Id.CompareTo(b.Id);
            });

            _used = new bool[_tiles.Count];
        }

        public IReadOnlyList<HandGroup> Run()
        {
            Recurse();

            return (_best ?? [])
                .Select(b => new HandGroup(b.Kind, b.Tiles, b.Needs, b.JokersUsed))
                .ToList();
        }

        private void Recurse()
        {
            if (_nodes++ > MaxNodes) return;

            var index = FirstUnused();

            if (index < 0)
            {
                Leaf();
                return;
            }

            // Nothing below this node can beat what is already banked, so stop walking it. Ties
            // are still explored, because the tie-break in section 6.4 is decided at the leaf.
            if (_best is not null && Optimistic(index).CompareTo(_bestScore) < 0) return;

            var tile = _tiles[index].Tile;
            var copies = CopiesOf(tile);

            // --- Kang, pung and pair on this face ---
            TryOfAKind(HandGroupKind.Kang, 4, copies);
            TryOfAKind(HandGroupKind.Pung, 3, copies);
            TryOfAKind(HandGroupKind.Pair, 2, copies);

            // --- Chow starting on this tile. Winds and dragons never form a run. ---
            if (tile.IsSuited && tile.Rank <= 7) TryChow(index, tile);

            // --- Two tiles that need exactly one more ---
            TryPartial(index, tile, step: 1);
            TryPartial(index, tile, step: 2);

            // --- Nothing at all ---
            _used[index] = true;
            _acc.Add(new Block(HandGroupKind.Floater, tile.Suit, tile.Rank, [_tiles[index]], [], 0));
            Recurse();
            _acc.RemoveAt(_acc.Count - 1);
            _used[index] = false;
        }

        /// <summary>
        /// A kang, pung or pair, built from as many real copies as the hand holds and jokers for
        /// the rest. Every split is tried, because spending a joker to leave a real copy behind can
        /// give the hand a pair it would not otherwise have.
        /// </summary>
        private void TryOfAKind(HandGroupKind kind, int size, List<int> copies)
        {
            var face = _tiles[copies[0]].Tile;

            for (var real = Math.Min(size, copies.Count); real >= 1; real--)
            {
                var wild = size - real;
                if (wild > JokersLeft) continue;

                var taken = copies.Take(real).ToList();
                foreach (var i in taken) _used[i] = true;

                var tiles = taken.Select(i => _tiles[i]).ToList();
                tiles.AddRange(TakeJokers(wild));

                _acc.Add(new Block(kind, face.Suit, face.Rank, tiles, [], wild));
                _jokersSpent += wild;

                Recurse();

                _jokersSpent -= wild;
                _acc.RemoveAt(_acc.Count - 1);
                foreach (var i in taken) _used[i] = false;
            }
        }

        /// <summary>A run starting on this tile, with a joker allowed to stand in for either of the other two.</summary>
        private void TryChow(int index, Tile low)
        {
            for (var wildSecond = 0; wildSecond <= 1; wildSecond++)
            for (var wildThird = 0; wildThird <= 1; wildThird++)
            {
                var wild = wildSecond + wildThird;
                if (wild > JokersLeft) continue;

                var second = wildSecond == 1 ? -1 : FindUnused(new Tile(low.Suit, low.Rank + 1));
                var third = wildThird == 1 ? -1 : FindUnused(new Tile(low.Suit, low.Rank + 2));

                if (wildSecond == 0 && second < 0) continue;
                if (wildThird == 0 && third < 0) continue;

                _used[index] = true;
                if (second >= 0) _used[second] = true;
                if (third >= 0) _used[third] = true;

                // Jokers are slotted where they stand in, so the block reads as the run it is.
                var spare = TakeJokers(wild);
                var next = 0;
                var tiles = new List<TileRef>
                {
                    _tiles[index],
                    second >= 0 ? _tiles[second] : spare[next++],
                    third >= 0 ? _tiles[third] : spare[next],
                };

                _acc.Add(new Block(HandGroupKind.Chow, low.Suit, low.Rank, tiles, [], wild));
                _jokersSpent += wild;

                Recurse();

                _jokersSpent -= wild;
                _acc.RemoveAt(_acc.Count - 1);

                if (third >= 0) _used[third] = false;
                if (second >= 0) _used[second] = false;
                _used[index] = false;
            }
        }

        /// <summary>
        /// Two tiles one short of a run: adjacent (<paramref name="step"/> 1) or a one-gap
        /// (<paramref name="step"/> 2). Real tiles only - a joker next to a tile is already a pair,
        /// which is a better reading of the same two tiles.
        /// </summary>
        private void TryPartial(int index, Tile low, int step)
        {
            if (!low.IsSuited || low.Rank + step > 9) return;

            var partner = FindUnused(new Tile(low.Suit, low.Rank + step));
            if (partner < 0) return;

            var needs = new List<Tile>();
            if (step == 1)
            {
                if (low.Rank - 1 >= 1) needs.Add(new Tile(low.Suit, low.Rank - 1));
                if (low.Rank + 2 <= 9) needs.Add(new Tile(low.Suit, low.Rank + 2));
            }
            else
            {
                needs.Add(new Tile(low.Suit, low.Rank + 1));
            }

            _used[index] = true;
            _used[partner] = true;

            _acc.Add(new Block(
                HandGroupKind.Partial, low.Suit, low.Rank, [_tiles[index], _tiles[partner]], needs, 0));

            Recurse();

            _acc.RemoveAt(_acc.Count - 1);
            _used[partner] = false;
            _used[index] = false;
        }

        /// <summary>
        /// Every tile is accounted for. Jokers nobody spent become floaters of their own rather
        /// than disappearing, then the arrangement is scored and kept if it beats the best so far.
        /// </summary>
        private void Leaf()
        {
            var groups = new List<Block>(_acc);

            foreach (var spare in _jokers.Skip(_jokersSpent))
                groups.Add(new Block(
                    HandGroupKind.Floater, spare.Tile.Suit, spare.Tile.Rank, [spare], [], 0));

            groups.Sort(Canonical);

            var score = ScoreOf(groups);

            if (_best is null || score.CompareTo(_bestScore) > 0 || (score.CompareTo(_bestScore) == 0 && Precedes(groups, _best)))
            {
                _best = groups;
                _bestScore = score;
            }
        }

        private Score ScoreOf(List<Block> groups)
        {
            var sets = _exposedSets + groups.Count(g => g.Kind is HandGroupKind.Kang or HandGroupKind.Pung or HandGroupKind.Chow);
            var pairs = groups.Count(g => g.Kind == HandGroupKind.Pair);
            var hasPair = pairs > 0 ? 1 : 0;

            // A sixth group can never be used by a winning hand, so counting it would let the
            // search prefer a hand that is further from finished. Everything found is still shown.
            var partials = pairs - hasPair + groups.Count(g => g.Kind == HandGroupKind.Partial);
            var useful = Math.Min(partials, Math.Max(0, UsefulGroups - sets));

            return new Score(sets, hasPair, useful, -groups.Sum(g => g.JokersUsed));
        }

        /// <summary>
        /// The best score still reachable from here, assuming every tile left falls into place.
        /// Only ever an over-estimate, so pruning on it can never discard the real best.
        /// </summary>
        private Score Optimistic(int from)
        {
            var left = _jokers.Count - _jokersSpent;
            for (var i = from; i < _used.Length; i++)
                if (!_used[i]) left++;

            var sets = _exposedSets
                + _acc.Count(g => g.Kind is HandGroupKind.Kang or HandGroupKind.Pung or HandGroupKind.Chow)
                + left / 3;

            var partials = Math.Min(left / 2, Math.Max(0, UsefulGroups - sets));

            return new Score(sets, 1, partials, -_jokersSpent);
        }

        // ------------------------------------------------------------------ small helpers

        private int JokersLeft => _jokers.Count - _jokersSpent;

        private List<TileRef> TakeJokers(int count) => _jokers.Skip(_jokersSpent).Take(count).ToList();

        private int FirstUnused()
        {
            for (var i = 0; i < _used.Length; i++)
                if (!_used[i]) return i;
            return -1;
        }

        private int FindUnused(Tile face)
        {
            for (var i = 0; i < _tiles.Count; i++)
                if (!_used[i] && _tiles[i].Tile == face) return i;
            return -1;
        }

        /// <summary>Every unused tile of this face, lowest id first. At most four.</summary>
        private List<int> CopiesOf(Tile face)
        {
            var found = new List<int>(4);
            for (var i = 0; i < _tiles.Count; i++)
                if (!_used[i] && _tiles[i].Tile == face) found.Add(i);
            return found;
        }

        /// <summary>
        /// Layout order, and with it the tie-break: kind first, then suit, then rank. Two
        /// arrangements that score the same are separated by this, so the same hand always comes
        /// back the same way round and the tiles do not jump about between draws.
        /// </summary>
        private static int Canonical(Block a, Block b)
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0) return byKind;

            var bySuit = a.Suit.CompareTo(b.Suit);
            if (bySuit != 0) return bySuit;

            var byRank = a.LowRank.CompareTo(b.LowRank);
            return byRank != 0 ? byRank : a.Tiles[0].Id.CompareTo(b.Tiles[0].Id);
        }

        private static bool Precedes(List<Block> candidate, List<Block> incumbent)
        {
            for (var i = 0; i < Math.Min(candidate.Count, incumbent.Count); i++)
            {
                var order = Canonical(candidate[i], incumbent[i]);
                if (order != 0) return order < 0;
            }

            return candidate.Count < incumbent.Count;
        }
    }
}
