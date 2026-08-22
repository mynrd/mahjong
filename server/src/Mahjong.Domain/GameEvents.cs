namespace Mahjong.Domain;

/// <summary>
/// Something that happened in a hand. Every event is appended to the game log, and most are also
/// pushed to the clients. Events carry only what the clients are allowed to know; anything
/// secret stays in <see cref="GameState"/>.
/// </summary>
public abstract record GameEvent
{
    /// <summary>Seat the event belongs to, or -1 for events that belong to the table.</summary>
    public int Seat { get; init; } = -1;
}

/// <summary>The joker face for this hand was picked.</summary>
public sealed record JokerChosen(Tile Joker) : GameEvent;

/// <summary>Tiles were dealt out. Which tiles is deliberately not part of the event.</summary>
public sealed record HandDealt(int HandNumber, int ManoSeat) : GameEvent;

/// <summary>A seat took a tile off the wall.</summary>
/// <param name="Replacement">True when it came from the tail after a bonus tile or a kang.</param>
public sealed record TileDrawn(TileRef Tile, bool Replacement) : GameEvent;

/// <summary>A bonus tile was turned face up in front of a seat.</summary>
public sealed record BonusExposed(TileRef Tile, int TotalSoFar) : GameEvent;

/// <summary>A seat earned an ambition and was paid on the spot.</summary>
public sealed record AmbitionEarned(Ambition Ambition, IReadOnlyList<Settlement> Settlements) : GameEvent;

/// <summary>A seat discarded.</summary>
public sealed record TileDiscarded(TileRef Tile) : GameEvent;

/// <summary>The other seats now have a limited time to claim the discard.</summary>
public sealed record ClaimWindowOpened(
    TileRef Tile,
    int FromSeat,
    DateTimeOffset DeadlineUtc,
    IReadOnlyDictionary<int, IReadOnlyList<ClaimKind>> AllowedBySeat) : GameEvent;

/// <summary>The claim window closed without anybody taking the tile.</summary>
public sealed record ClaimWindowClosed(TileRef Tile) : GameEvent;

/// <summary>A group was laid on the table.</summary>
public sealed record MeldFormed(ExposedMeld Meld) : GameEvent;

/// <summary>It is now this seat's turn.</summary>
public sealed record TurnChanged(GamePhase Phase) : GameEvent;

/// <summary>The hand is over.</summary>
public sealed record HandEnded(HandOutcome Outcome) : GameEvent;
