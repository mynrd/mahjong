using System.Text.Json.Serialization;

namespace Mahjong.Domain;

/// <summary>
/// The shapes a group of tiles can take. "Bahay" is the Filipino term for a set of three;
/// a kang is four tiles but still counts as one bahay, because the replacement draw that
/// follows a kang keeps the hand arithmetic at 17 tiles.
/// </summary>
public enum SetKind
{
    /// <summary>Two identical tiles. The "eye" or "mother". Exactly one per winning hand.</summary>
    Pair,

    /// <summary>Three tiles in a run, same suit, e.g. 4-5-6 bamboo.</summary>
    Chow,

    /// <summary>Three identical tiles.</summary>
    Pung,

    /// <summary>Four identical tiles.</summary>
    Kang,
}

/// <summary>
/// A group of tiles a player has laid face up on the table (or face down, for a secret kang).
/// Once exposed, these tiles can no longer be discarded or rearranged.
/// </summary>
/// <param name="Kind">Chow, Pung or Kang. A <see cref="SetKind.Pair"/> is never exposed.</param>
/// <param name="Tiles">The physical tiles, 3 for a chow or pung, 4 for a kang.</param>
/// <param name="Concealed">
/// True for a secret kang: all four tiles were drawn by the player, so the set stays face down
/// and does not break an "all up" (fully concealed) hand.
/// </param>
/// <param name="ClaimedFromSeat">Seat the tile was taken from, or null if fully self-drawn.</param>
/// <param name="FromSagasa">True if this kang started as an exposed pung and was later extended.</param>
public sealed record ExposedMeld(
    SetKind Kind,
    IReadOnlyList<TileRef> Tiles,
    bool Concealed = false,
    int? ClaimedFromSeat = null,
    bool FromSagasa = false)
{
    /// <summary>The face this meld is built on. For a chow, the lowest of the three.</summary>
    [JsonIgnore]
    public Tile BaseTile => Kind == SetKind.Chow
        ? Tiles.Select(t => t.Tile).OrderBy(t => t.Rank).First()
        : Tiles[0].Tile;

    /// <summary>Projects the meld into the form the scorer works with.</summary>
    public HandSet ToHandSet() => new(Kind, BaseTile.Suit, BaseTile.Rank, JokersUsed: 0);
}

/// <summary>
/// One group inside a completed hand, as produced by <see cref="HandAnalyzer"/>. Unlike
/// <see cref="ExposedMeld"/> this is a face-level description, because a joker standing in for
/// a tile has no physical tile of that face behind it.
/// </summary>
/// <param name="LowRank">
/// The rank the set is built on. For a chow, the lowest of the three. Zero means the set was
/// formed entirely out of jokers and has no fixed suit or rank, see <see cref="IsWildcard"/>.
/// </param>
/// <param name="JokersUsed">How many jokers were consumed to complete this set.</param>
public readonly record struct HandSet(SetKind Kind, Suit Suit, int LowRank, int JokersUsed)
{
    /// <summary>
    /// True when the set is made purely of jokers, so it has no committed suit or rank. This can
    /// only happen with 3 or more spare jokers and is treated as compatible with any pattern.
    /// </summary>
    [JsonIgnore]
    public bool IsWildcard => LowRank == 0;

    /// <summary>The tile faces this set represents. Empty for a wildcard set.</summary>
    [JsonIgnore]
    public IEnumerable<Tile> Faces
    {
        get
        {
            if (IsWildcard) yield break;

            switch (Kind)
            {
                case SetKind.Chow:
                    yield return new Tile(Suit, LowRank);
                    yield return new Tile(Suit, LowRank + 1);
                    yield return new Tile(Suit, LowRank + 2);
                    break;
                case SetKind.Pair:
                    yield return new Tile(Suit, LowRank);
                    yield return new Tile(Suit, LowRank);
                    break;
                case SetKind.Pung:
                    for (var i = 0; i < 3; i++) yield return new Tile(Suit, LowRank);
                    break;
                case SetKind.Kang:
                    for (var i = 0; i < 4; i++) yield return new Tile(Suit, LowRank);
                    break;
            }
        }
    }

    public override string ToString() => IsWildcard
        ? $"{Kind}(joker x{JokersUsed})"
        : Kind == SetKind.Chow
            ? $"Chow {new Tile(Suit, LowRank).Code}-{LowRank + 2}"
            : $"{Kind} {new Tile(Suit, LowRank).Code}";
}

/// <summary>
/// One complete way of reading a hand as sets plus a pair. A single hand can often be read more
/// than one way (for example 1-1-1-2-3 is either a pung of 1s plus 2-3, or 1 plus a 1-2-3 chow),
/// which is why the scorer evaluates every reading and keeps the highest-paying one.
/// </summary>
/// <param name="Sets">Every group in the hand, including the exposed melds and the pair.</param>
/// <param name="IsSietePares">True when the hand was read as seven pairs plus one set.</param>
public sealed record Decomposition(IReadOnlyList<HandSet> Sets, bool IsSietePares = false)
{
    [JsonIgnore]
    public HandSet Pair => Sets.First(s => s.Kind == SetKind.Pair);

    /// <summary>Every group except the pair.</summary>
    [JsonIgnore]
    public IEnumerable<HandSet> Bahay => Sets.Where(s => s.Kind != SetKind.Pair);

    [JsonIgnore]
    public int JokersUsed => Sets.Sum(s => s.JokersUsed);

    public override string ToString() => string.Join(" | ", Sets);
}
