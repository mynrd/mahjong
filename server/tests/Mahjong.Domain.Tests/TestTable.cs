using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

/// <summary>
/// Builds a game state with hands chosen by the test rather than dealt at random, so a specific
/// situation (a contested discard, a kang, a wait) can be set up exactly. Tile ids are handed out
/// from one shared pool, so no two seats can end up holding the same physical tile.
/// </summary>
internal sealed class TestTable
{
    private readonly HashSet<int> _used = [];
    private readonly List<TileRef> _forcedDraws = [];

    public GameState State { get; private set; } = null!;

    public static TestTable Build(
        Action<TestTable> setup,
        int currentSeat = 0,
        GamePhase phase = GamePhase.AwaitingDiscard,
        int manoSeat = 0,
        RuleOptions? rules = null,
        Tile? joker = null)
    {
        var table = new TestTable();

        table.State = new GameState
        {
            // Joker off by default: a random wild face would make hand-built scenarios ambiguous.
            Rules = rules ?? (RuleOptions.Default with { JokerEnabled = false }),
            HandNumber = 1,
            ManoSeat = manoSeat,
            Seed = 1,
            Wall = TileSet.All.ToList(),
            FrontIndex = 0,
            BackIndex = TileSet.TotalTiles - 1,
            CurrentSeat = currentSeat,
            Phase = phase,
            Joker = joker,
        };

        setup(table);

        // Everything the test dealt by hand has to come out of the wall, otherwise the same
        // physical tile could be drawn again mid-test.
        table.State.Wall.RemoveAll(t => table._used.Contains(t.Id));

        // Shuffle what is left. TileSet.All is in canonical order, which puts all 36 bonus tiles
        // at the tail - exactly where replacement draws come from. Left unshuffled, a single kang
        // would turn up thirteen flowers in a row and fire an ambition the test never asked for.
        var rng = new Random(20260822);
        for (var i = table.State.Wall.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (table.State.Wall[i], table.State.Wall[j]) = (table.State.Wall[j], table.State.Wall[i]);
        }

        // Forced draws go in after the shuffle, otherwise they would be scattered again.
        table.State.Wall.InsertRange(table.State.FrontIndex, table._forcedDraws);
        table.State.BackIndex = table.State.Wall.Count - 1;

        return table;
    }

    /// <summary>Puts tiles in a seat's concealed hand, taking each from the shared pool.</summary>
    public TestTable Hand(int seat, string notation)
    {
        State.Hands[seat].Concealed.AddRange(Take(notation));
        return this;
    }

    /// <summary>Puts an already-exposed group in front of a seat.</summary>
    public TestTable Meld(int seat, SetKind kind, string notation, bool concealed = false, int? claimedFrom = 1)
    {
        State.Hands[seat].Melds.Add(new ExposedMeld(kind, Take(notation), concealed, concealed ? null : claimedFrom));
        return this;
    }

    /// <summary>Exposes bonus tiles in front of a seat.</summary>
    public TestTable Bonus(int seat, string notation)
    {
        State.Hands[seat].Bonus.AddRange(Take(notation));
        return this;
    }

    /// <summary>
    /// Pads a seat's hand out to size with tiles the test does not care about. Any face listed in
    /// <paramref name="avoid"/>, and anything within two ranks of it in the same suit, is skipped,
    /// so the padding can never accidentally make the seat able to pung, kang or chow the tile the
    /// test is about. Copies are spread one face at a time rather than four at a time, so filler
    /// hands do not quietly contain pungs.
    /// </summary>
    public TestTable Filler(int seat, int count, params string[] avoid)
    {
        var blocked = avoid.Select(Tile.Parse).ToList();

        bool NearAvoided(Tile tile) =>
            blocked.Any(a => a.Suit == tile.Suit && Math.Abs(a.Rank - tile.Rank) <= 2);

        var added = 0;

        for (var pass = 0; pass < 4 && added < count; pass++)
        {
            for (var step = 0; step < TileSet.PlayableFaces && added < count; step++)
            {
                // Offset by seat so the four seats do not all get the same shape.
                var faceIndex = (step + seat * 7) % TileSet.PlayableFaces;
                var face = Tile.FromPlayableIndex(faceIndex);
                if (NearAvoided(face)) continue;

                var id = -1;
                for (var candidate = 0; candidate < TileSet.PlayableTiles; candidate++)
                {
                    if (_used.Contains(candidate) || TileSet.Canonical[candidate] != face) continue;
                    id = candidate;
                    break;
                }

                if (id < 0) continue;

                _used.Add(id);
                State.Hands[seat].Concealed.Add(new TileRef(id));
                added++;
            }
        }

        if (added < count)
            throw new InvalidOperationException($"Could only find {added} filler tiles for seat {seat}, wanted {count}.");

        return this;
    }

    /// <summary>Finds the id of a tile the given seat is holding, so it can be discarded.</summary>
    public int HeldId(int seat, string face)
    {
        var wanted = Tile.Parse(face);
        var match = State.Hands[seat].Concealed.FirstOrDefault(t => t.Tile == wanted, new TileRef(-1));

        if (match.Id < 0)
            throw new InvalidOperationException(
                $"Seat {seat} does not hold {face}. Hand is {TileNotation.Format(State.Hands[seat].Concealed)}.");

        return match.Id;
    }

    /// <summary>
    /// Forces the next tiles drawn from the front of the wall, in the order given. Applied once
    /// the wall has been cleaned up and shuffled, so the order actually survives.
    /// </summary>
    public TestTable NextDraw(string notation)
    {
        _forcedDraws.AddRange(Take(notation));
        return this;
    }

    /// <summary>Total tiles accounted for anywhere: hands, melds, bonuses, discards and wall.</summary>
    public int TilesAccountedFor() =>
        State.Hands.Sum(h => h.Concealed.Count + h.Melds.Sum(m => m.Tiles.Count) + h.Bonus.Count)
        + State.Discards.Count(d => !d.Claimed)
        + Math.Max(0, State.BackIndex - State.FrontIndex + 1);

    private List<TileRef> Take(string notation)
    {
        var faces = TileNotation.Parse(notation);
        var taken = new List<TileRef>(faces.Count);

        foreach (var face in faces)
        {
            var id = -1;
            for (var candidate = 0; candidate < TileSet.TotalTiles; candidate++)
            {
                if (_used.Contains(candidate) || TileSet.Canonical[candidate] != face) continue;
                id = candidate;
                break;
            }

            if (id < 0) throw new InvalidOperationException($"Ran out of copies of {face} building the table.");

            _used.Add(id);
            taken.Add(new TileRef(id));
        }

        return taken;
    }
}
