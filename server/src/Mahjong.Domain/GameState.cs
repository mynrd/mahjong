using System.Text.Json.Serialization;

namespace Mahjong.Domain;

/// <summary>Where a hand is in its turn cycle.</summary>
public enum GamePhase
{
    /// <summary>The current seat has to take a tile off the wall.</summary>
    AwaitingDraw,

    /// <summary>The current seat is holding one tile too many and has to discard or declare.</summary>
    AwaitingDiscard,

    /// <summary>A tile has just been discarded and the other three seats may claim it.</summary>
    AwaitingClaims,

    /// <summary>The hand is finished.</summary>
    HandOver,
}

/// <summary>Why a hand stopped.</summary>
public enum HandEndReason
{
    /// <summary>Somebody declared a complete hand.</summary>
    Todas,

    /// <summary>The wall ran out with nobody complete. Nobody pays.</summary>
    WallExhausted,

    /// <summary>The mano was dealt a complete hand before play began.</summary>
    Bisaklat,
}

/// <summary>The kinds of claim that can be made on a discarded tile.</summary>
public enum ClaimKind
{
    Chow,
    Pung,
    Kang,
    Todas,
}

/// <summary>A tile that was discarded, and whether somebody took it.</summary>
public sealed record DiscardedTile(int Seat, TileRef Tile, bool Claimed = false);

/// <summary>One seat's tiles.</summary>
public sealed class PlayerHand
{
    /// <summary>Tiles still hidden in hand. Never contains a bonus tile.</summary>
    public List<TileRef> Concealed { get; init; } = [];

    /// <summary>Groups laid on the table, plus any secret kangs.</summary>
    public List<ExposedMeld> Melds { get; init; } = [];

    /// <summary>Bonus tiles exposed in front of the player. Never part of the hand proper.</summary>
    public List<TileRef> Bonus { get; init; } = [];

    /// <summary>How many tiles this seat is holding, which is 16 between turns and 17 during one.</summary>
    [JsonIgnore]
    public int TileCount => Concealed.Count + Melds.Sum(m => m.Kind == SetKind.Kang ? 3 : m.Tiles.Count);

    [JsonIgnore]
    public IReadOnlyList<Tile> ConcealedFaces => Concealed.Select(t => t.Tile).ToArray();

    public bool Has(Tile face, int atLeast = 1) => Concealed.Count(t => t.Tile == face) >= atLeast;
}

/// <summary>
/// One seat's declared claim, with the tiles from its own hand that the meld will be built from.
/// </summary>
/// <param name="TileIds">
/// Empty when the seat did not name any tiles, in which case the server picks them itself when the
/// window closes. Todas is always empty: the win is read off the whole hand.
/// </param>
public sealed record DeclaredClaim(ClaimKind Kind, IReadOnlyList<int> TileIds);

/// <summary>The open claim window on a discard.</summary>
public sealed class PendingClaim
{
    public required TileRef Tile { get; init; }
    public required int FromSeat { get; init; }
    public required DateTimeOffset DeadlineUtc { get; init; }

    /// <summary>Seats that have said they do not want the tile.</summary>
    public HashSet<int> Passed { get; init; } = [];

    /// <summary>Claims declared so far, by seat.</summary>
    public Dictionary<int, DeclaredClaim> Declared { get; init; } = [];
}

/// <summary>How the hand ended and what it cost.</summary>
public sealed record HandOutcome(
    HandEndReason Reason,
    int? WinnerSeat,
    HandScore? Score,
    IReadOnlyList<Settlement> Settlements);

/// <summary>
/// The complete state of one hand, server-side. This is the type that gets snapshotted to
/// <c>Games.StateJson</c>. It holds everything, including tiles no player is allowed to see, so
/// it must never be sent to a client. <see cref="PlayerView"/> is what goes on the wire.
/// </summary>
public sealed class GameState
{
    public required RuleOptions Rules { get; init; }

    /// <summary>Which hand of the session this is, counting from 1.</summary>
    public required int HandNumber { get; init; }

    /// <summary>Seat that deals and discards first.</summary>
    public required int ManoSeat { get; init; }

    /// <summary>Seed the wall was shuffled with, kept so a hand can be replayed exactly.</summary>
    public required int Seed { get; init; }

    /// <summary>The wild face for this hand, or null when the joker rule is off.</summary>
    public Tile? Joker { get; set; }

    /// <summary>All 144 tiles in shuffled order.</summary>
    public required List<TileRef> Wall { get; init; }

    /// <summary>Next tile a normal draw takes. Moves forward.</summary>
    public int FrontIndex { get; set; }

    /// <summary>
    /// Next tile a replacement draw takes, after a bonus tile or a kang. Moves backward from the
    /// tail. When it crosses <see cref="FrontIndex"/> the wall is spent and the hand is drawn.
    /// </summary>
    public int BackIndex { get; set; }

    public PlayerHand[] Hands { get; init; } = [new(), new(), new(), new()];

    public List<DiscardedTile> Discards { get; init; } = [];

    public int CurrentSeat { get; set; }

    public GamePhase Phase { get; set; } = GamePhase.AwaitingDraw;

    public PendingClaim? Pending { get; set; }

    public HandOutcome? Outcome { get; set; }

    /// <summary>
    /// The tile the current seat just drew, if any. Needed to tell a self-drawn win (bunot) from
    /// a claimed one, and to know which tile a sagasa used.
    /// </summary>
    public TileRef? JustDrew { get; set; }

    /// <summary>How many tiles are left to draw normally.</summary>
    [JsonIgnore]
    public int TilesRemaining => Math.Max(0, BackIndex - FrontIndex + 1);

    [JsonIgnore]
    public bool WallExhausted => FrontIndex > BackIndex;

    /// <summary>Seat that plays after the given one. Play runs counter-clockwise, seat 0 to 3.</summary>
    public static int NextSeat(int seat) => (seat + 1) % 4;

    /// <summary>True when <paramref name="claimant"/> sits immediately after <paramref name="discarder"/>.</summary>
    public static bool IsLeftOf(int claimant, int discarder) => NextSeat(discarder) == claimant;
}
