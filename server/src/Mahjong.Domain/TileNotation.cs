using System.Text;

namespace Mahjong.Domain;

/// <summary>
/// Compact text form for a group of tiles, so hands can be written down in one short string
/// instead of a list of constructor calls. Used by the tests, the seeded-hand debug endpoint and
/// the game log.
///
/// A group is one or more digits followed by a suit letter:
/// <c>d</c> dots, <c>b</c> bamboo, <c>c</c> characters, <c>w</c> wind, <c>r</c> dragon,
/// <c>f</c> flower, <c>s</c> season. Whitespace between groups is ignored.
///
/// <code>
/// "123d 456d 789d 111b 22c"   ->  1,2,3 dots  4,5,6 dots  7,8,9 dots  three 1-bamboo  two 2-chars
/// </code>
/// </summary>
public static class TileNotation
{
    public static IReadOnlyList<Tile> Parse(string notation)
    {
        ArgumentNullException.ThrowIfNull(notation);

        var tiles = new List<Tile>();
        var pending = new List<int>();

        foreach (var raw in notation)
        {
            if (char.IsWhiteSpace(raw)) continue;

            if (char.IsAsciiDigit(raw))
            {
                pending.Add(raw - '0');
                continue;
            }

            var suit = SuitOf(raw, notation);

            if (pending.Count == 0)
                throw new FormatException($"Suit letter '{raw}' in \"{notation}\" has no digits before it.");

            var maxRank = Tile.MaxRank(suit);
            foreach (var rank in pending)
            {
                if (rank < 1 || rank > maxRank)
                    throw new FormatException($"Rank {rank} is out of range for suit {suit} in \"{notation}\".");

                tiles.Add(new Tile(suit, rank));
            }

            pending.Clear();
        }

        if (pending.Count > 0)
            throw new FormatException($"\"{notation}\" ends with digits that have no suit letter.");

        return tiles;
    }

    /// <summary>Parses and then wraps each face in a distinct id, as if dealt from a fresh set.</summary>
    public static IReadOnlyList<TileRef> ParseRefs(string notation)
    {
        var wanted = Parse(notation);
        var used = new HashSet<int>();
        var refs = new List<TileRef>(wanted.Count);

        foreach (var face in wanted)
        {
            var id = -1;
            for (var candidate = 0; candidate < TileSet.TotalTiles; candidate++)
            {
                if (used.Contains(candidate) || TileSet.Canonical[candidate] != face) continue;
                id = candidate;
                break;
            }

            if (id < 0)
                throw new InvalidOperationException($"\"{notation}\" asks for a fifth copy of {face}; only four exist.");

            used.Add(id);
            refs.Add(new TileRef(id));
        }

        return refs;
    }

    /// <summary>Renders tiles back to the compact form, grouped by suit and sorted.</summary>
    public static string Format(IEnumerable<Tile> tiles)
    {
        var groups = tiles
            .GroupBy(t => t.Suit)
            .OrderBy(g => g.Key);

        var sb = new StringBuilder();
        foreach (var group in groups)
        {
            if (sb.Length > 0) sb.Append(' ');
            foreach (var tile in group.OrderBy(t => t.Rank)) sb.Append(tile.Rank);
            sb.Append(SuitLetter(group.Key));
        }

        return sb.ToString();
    }

    public static string Format(IEnumerable<TileRef> tiles) => Format(tiles.Select(t => t.Tile));

    private static Suit SuitOf(char letter, string notation) => char.ToLowerInvariant(letter) switch
    {
        'd' => Suit.Dots,
        'b' => Suit.Bamboo,
        'c' => Suit.Chars,
        'w' => Suit.Wind,
        'r' => Suit.Dragon,
        'f' => Suit.Flower,
        's' => Suit.Season,
        _ => throw new FormatException($"Unknown suit letter '{letter}' in \"{notation}\"."),
    };

    private static char SuitLetter(Suit suit) => suit switch
    {
        Suit.Dots => 'd',
        Suit.Bamboo => 'b',
        Suit.Chars => 'c',
        Suit.Wind => 'w',
        Suit.Dragon => 'r',
        Suit.Flower => 'f',
        Suit.Season => 's',
        _ => throw new ArgumentOutOfRangeException(nameof(suit), suit, null),
    };
}
