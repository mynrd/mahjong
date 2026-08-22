namespace Mahjong.Domain;

/// <summary>
/// Tile families. Dots, bamboo, characters, winds and dragons all sit in the hand and form sets.
/// Only flowers and seasons are bonus tiles: exposed on draw and replaced.
/// </summary>
public enum Suit
{
    /// <summary>Balls / circles / "bulaklak". Ranks 1-9.</summary>
    Dots = 0,

    /// <summary>Sticks / bamboo / "kahoy". Ranks 1-9.</summary>
    Bamboo = 1,

    /// <summary>Characters / "letra". Ranks 1-9.</summary>
    Chars = 2,

    /// <summary>1 East, 2 South, 3 West, 4 North. Held in hand; pungs and kangs only, no runs.</summary>
    Wind = 3,

    /// <summary>1 Red, 2 Green, 3 White. Held in hand; pungs and kangs only, no runs.</summary>
    Dragon = 4,

    /// <summary>1 Plum, 2 Orchid, 3 Chrysanthemum, 4 Bamboo. Bonus tile.</summary>
    Flower = 5,

    /// <summary>1 Spring, 2 Summer, 3 Autumn, 4 Winter. Bonus tile.</summary>
    Season = 6,
}

/// <summary>
/// A tile face. Two physical tiles with the same face are equal as <see cref="Tile"/> values;
/// use <see cref="TileRef"/> when the individual physical tile has to stay distinguishable.
/// </summary>
public readonly record struct Tile(Suit Suit, int Rank)
{
    /// <summary>True for everything except flowers and seasons: the tiles that can be held.</summary>
    public bool IsPlayable => Suit <= Suit.Dragon;

    /// <summary>True for flowers and seasons, which are exposed on draw and replaced.</summary>
    public bool IsBonus => !IsPlayable;

    /// <summary>
    /// True for the three numbered suits, the only tiles that can form a run. Winds and dragons
    /// are playable but not suited: E-S-W is not a chow and neither is red-green-white.
    /// </summary>
    public bool IsSuited => Suit <= Suit.Chars;

    /// <summary>True for rank 1 and rank 9 of a numbered suit.</summary>
    public bool IsTerminal => IsSuited && (Rank == 1 || Rank == 9);

    /// <summary>
    /// Index 0-33 used by the win-detection counts array. The three numbered suits take 0-26 as
    /// suit * 9 + (rank - 1), then the four winds take 27-30 and the three dragons 31-33. Runs are
    /// only ever formed inside the first 27, so no run can walk off the end of a suit.
    /// Throws for bonus tiles, which never take part in a hand.
    /// </summary>
    public int PlayableIndex => Suit switch
    {
        <= Suit.Chars => (int)Suit * 9 + Rank - 1,
        Suit.Wind => SuitedFaces + Rank - 1,
        Suit.Dragon => SuitedFaces + 4 + Rank - 1,
        _ => throw new InvalidOperationException($"{this} is a bonus tile and has no playable index."),
    };

    /// <summary>Number of distinct faces in the three numbered suits: the 0-26 block.</summary>
    internal const int SuitedFaces = 27;

    /// <summary>Rebuilds a tile from a 0-33 playable index.</summary>
    public static Tile FromPlayableIndex(int index) => index switch
    {
        >= 0 and < SuitedFaces => new Tile((Suit)(index / 9), index % 9 + 1),
        >= SuitedFaces and < SuitedFaces + 4 => new Tile(Suit.Wind, index - SuitedFaces + 1),
        >= SuitedFaces + 4 and < TileSet.PlayableFaces => new Tile(Suit.Dragon, index - SuitedFaces - 4 + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Playable index must be 0-33."),
    };

    /// <summary>
    /// Short wire code, e.g. "D5" five dots, "B1" one bamboo, "C9" nine characters,
    /// "W1" east, "R1" red dragon, "F2" second flower, "S3" third season.
    /// </summary>
    public string Code => $"{SuitLetter(Suit)}{Rank}";

    public static Tile Parse(string code)
    {
        if (code is not { Length: 2 })
            throw new FormatException($"Tile code '{code}' must be two characters.");

        var suit = code[0] switch
        {
            'D' => Suit.Dots,
            'B' => Suit.Bamboo,
            'C' => Suit.Chars,
            'W' => Suit.Wind,
            'R' => Suit.Dragon,
            'F' => Suit.Flower,
            'S' => Suit.Season,
            _ => throw new FormatException($"Unknown suit letter '{code[0]}' in tile code '{code}'."),
        };

        if (!int.TryParse(code[1..], out var rank) || rank < 1 || rank > MaxRank(suit))
            throw new FormatException($"Rank '{code[1..]}' is out of range for suit {suit}.");

        return new Tile(suit, rank);
    }

    public static int MaxRank(Suit suit) => suit switch
    {
        Suit.Dots or Suit.Bamboo or Suit.Chars => 9,
        Suit.Wind => 4,
        Suit.Dragon => 3,
        Suit.Flower or Suit.Season => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(suit), suit, null),
    };

    private static char SuitLetter(Suit suit) => suit switch
    {
        Suit.Dots => 'D',
        Suit.Bamboo => 'B',
        Suit.Chars => 'C',
        Suit.Wind => 'W',
        Suit.Dragon => 'R',
        Suit.Flower => 'F',
        Suit.Season => 'S',
        _ => throw new ArgumentOutOfRangeException(nameof(suit), suit, null),
    };

    public override string ToString() => Code;
}

/// <summary>
/// One physical tile out of the 144. <see cref="Id"/> is its index in
/// <see cref="TileSet.Canonical"/> and stays stable for the whole hand, so the client can
/// address "this specific 5-dots" rather than "a 5-dots".
/// </summary>
public readonly record struct TileRef(int Id)
{
    public Tile Tile => TileSet.Canonical[Id];

    public override string ToString() => $"{Tile.Code}#{Id}";
}

/// <summary>The fixed 144-tile Filipino Mahjong set.</summary>
public static class TileSet
{
    public const int TotalTiles = 144;
    public const int PlayableTiles = 136;
    public const int BonusTiles = 8;

    /// <summary>Number of distinct playable faces: 3 suits x 9 ranks, 4 winds, 3 dragons.</summary>
    public const int PlayableFaces = 34;

    /// <summary>
    /// Every physical tile, indexed by <see cref="TileRef.Id"/>. Playable tiles occupy ids
    /// 0-135 and bonus tiles 136-143, so <c>id &lt; 136</c> is a cheap playability test.
    /// </summary>
    public static readonly IReadOnlyList<Tile> Canonical = Build();

    /// <summary>All 144 tile ids, in canonical order.</summary>
    public static IEnumerable<TileRef> All => Enumerable.Range(0, TotalTiles).Select(i => new TileRef(i));

    private static Tile[] Build()
    {
        var tiles = new List<Tile>(TotalTiles);

        // 108 suited: three suits, ranks 1-9, four copies each.
        foreach (var suit in new[] { Suit.Dots, Suit.Bamboo, Suit.Chars })
            for (var rank = 1; rank <= 9; rank++)
                for (var copy = 0; copy < 4; copy++)
                    tiles.Add(new Tile(suit, rank));

        // 28 honors: 16 winds, 12 dragons. Playable, so they stay inside the 0-135 block.
        for (var rank = 1; rank <= 4; rank++)
            for (var copy = 0; copy < 4; copy++)
                tiles.Add(new Tile(Suit.Wind, rank));

        for (var rank = 1; rank <= 3; rank++)
            for (var copy = 0; copy < 4; copy++)
                tiles.Add(new Tile(Suit.Dragon, rank));

        // 8 bonus: 4 flowers, 4 seasons, one of each.
        for (var rank = 1; rank <= 4; rank++)
            tiles.Add(new Tile(Suit.Flower, rank));

        for (var rank = 1; rank <= 4; rank++)
            tiles.Add(new Tile(Suit.Season, rank));

        if (tiles.Count != TotalTiles)
            throw new InvalidOperationException($"Built {tiles.Count} tiles, expected {TotalTiles}.");

        return tiles.ToArray();
    }
}
