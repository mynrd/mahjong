using System.ComponentModel.DataAnnotations;

namespace Mahjong.Infrastructure;

/// <summary>Where a room is in its life.</summary>
public enum RoomStatus
{
    /// <summary>Waiting for the other seats to be taken.</summary>
    Lobby,

    /// <summary>A hand is in progress.</summary>
    Playing,

    /// <summary>The host closed the room.</summary>
    Closed,
}

public enum GameStatus
{
    InProgress,

    /// <summary>Played to a result: somebody declared, or the wall ran out.</summary>
    Finished,

    /// <summary>
    /// Stopped part-way, because the host closed the table under it. Kept apart from
    /// <see cref="Finished"/> so a hand that never reached a result stays out of the replay list -
    /// it has no winner, no settlements and no ending to step to.
    /// </summary>
    Abandoned,
}

/// <summary>
/// A table. The room code goes in the invite link and the password gates who can sit down.
/// There are no user accounts anywhere in this schema on purpose: a room plus a seat is the whole
/// identity model.
/// </summary>
public class Room
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Six characters from an unambiguous alphabet, used in the invite link.</summary>
    [MaxLength(6)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>PBKDF2-SHA256 of the room password. Never the password itself.</summary>
    [MaxLength(64)]
    public byte[] PasswordHash { get; set; } = [];

    [MaxLength(32)]
    public byte[] PasswordSalt { get; set; } = [];

    public int PasswordIterations { get; set; }

    /// <summary>The room's <c>RuleOptions</c>, serialised, so house rules travel with the room.</summary>
    public string RulesJson { get; set; } = string.Empty;

    public RoomStatus Status { get; set; } = RoomStatus.Lobby;

    /// <summary>Seat that may start hands and add bots. Always the seat that created the room.</summary>
    public Guid? HostPlayerId { get; set; }

    /// <summary>Hands played so far, used to rotate the mano seat.</summary>
    public int HandsPlayed { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Player> Players { get; set; } = [];
    public List<Game> Games { get; set; } = [];
    public List<ReplayToken> ReplayTokens { get; set; } = [];
}

/// <summary>
/// Proof that somebody knew a room's password, good for reading its finished hands back.
///
/// A replay is opened from a link, often in a browser that never took a seat, so the seat token is
/// no help. Verifying the password costs 210,000 PBKDF2 iterations, which is fine once but not on
/// every request, so it is exchanged for one of these. Only the hash is stored, for the same
/// reason <see cref="Player.TokenHash"/> is.
/// </summary>
public class ReplayToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    /// <summary>SHA-256 of the bearer token handed out when the password was accepted.</summary>
    [MaxLength(32)]
    public byte[] TokenHash { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Somebody sitting at a table. A player exists only inside one room, and their identity is the
/// bearer token they were handed when they joined, which is why only its hash is stored.
/// </summary>
public class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    [MaxLength(24)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>0 to 3. Unique within a room.</summary>
    public int Seat { get; set; }

    /// <summary>SHA-256 of the bearer token issued at join time.</summary>
    [MaxLength(32)]
    public byte[] TokenHash { get; set; } = [];

    public bool IsBot { get; set; }

    /// <summary>Whether a live connection is currently attached. A disconnect never frees the seat.</summary>
    public bool IsConnected { get; set; }

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Running total across every hand in this room, in scoring units.</summary>
    public int Balance { get; set; }
}

/// <summary>One hand. The state snapshot is what a reconnecting client is rebuilt from.</summary>
public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public int HandNumber { get; set; }

    public int ManoSeat { get; set; }

    /// <summary>Wire code of the joker face, or null when the joker rule is off.</summary>
    [MaxLength(3)]
    public string? JokerTile { get; set; }

    /// <summary>Seed the wall was shuffled with, so any hand can be replayed exactly.</summary>
    public int Seed { get; set; }

    /// <summary>
    /// The whole <c>GameState</c>, serialised. This holds every player's tiles and the order of
    /// the wall, so it must never be handed to a client as-is.
    /// </summary>
    public string StateJson { get; set; } = string.Empty;

    public GameStatus Status { get; set; } = GameStatus.InProgress;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }

    public List<GameAction> Actions { get; set; } = [];
    public List<GameFrame> Frames { get; set; } = [];
    public List<HandArrangement> Arrangements { get; set; } = [];
    public HandResult? Result { get; set; }
}

/// <summary>
/// The whole state after one move, kept so a finished hand can be stepped through afterwards.
///
/// <see cref="Game.StateJson"/> is overwritten on every move, so it only ever holds the position
/// the hand ended in. The action log cannot stand in for the missing history either: events carry
/// no hidden tiles on purpose, so nothing in it says who was holding what. Hence a row per move.
///
/// Same rule as <see cref="Game.StateJson"/>: this holds every seat's tiles and the order of the
/// wall, so it is never handed to a client as-is.
/// </summary>
public class GameFrame
{
    public long Id { get; set; }

    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    /// <summary>Seq of the last <see cref="GameAction"/> this frame includes. Unique per game.</summary>
    public int AfterSeq { get; set; }

    /// <summary>The whole <c>GameState</c>, serialised, exactly as it stood after that action.</summary>
    public string StateJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// How one player chose to lay their own tiles out this hand.
///
/// Purely how the hand is drawn on that player's screen - which tiles they pushed together into a
/// group - and never anything the rules care about. It is here rather than in the browser because
/// a phone that sleeps mid-hand reconnects with a fresh page, and losing the arrangement you just
/// built by hand every time the screen locks makes the feature not worth having.
///
/// Scoped to a game, because the tile ids it holds only mean anything inside one hand.
/// </summary>
public class HandArrangement
{
    public long Id { get; set; }

    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    public Guid PlayerId { get; set; }

    /// <summary>Tile ids as the player grouped them, e.g. <c>[[3,17,42],[8,9]]</c>.</summary>
    public string GroupsJson { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One thing a player did, in order. The snapshot on <see cref="Game.StateJson"/> is what makes
/// reconnects fast; this log is what makes a disputed hand reconstructible after the fact.
/// </summary>
public class GameAction
{
    public long Id { get; set; }

    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    /// <summary>Position in the hand, starting at 1. Unique per game.</summary>
    public int Seq { get; set; }

    public Guid? PlayerId { get; set; }

    public int Seat { get; set; }

    [MaxLength(40)]
    public string ActionType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>How a hand finished and what it was worth.</summary>
public class HandResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    /// <summary>Null when the wall ran out with nobody complete.</summary>
    public Guid? WinnerPlayerId { get; set; }

    public int? WinnerSeat { get; set; }

    [MaxLength(24)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>The scoring breakdown, so a table can see exactly why it paid what it paid.</summary>
    public string BreakdownJson { get; set; } = string.Empty;

    public int TotalUnits { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SettlementRow> Settlements { get; set; } = [];
}

/// <summary>
/// One movement of money. Ambitions are written the moment they are declared, mid-hand, so a
/// row here is not necessarily tied to the end of the hand.
/// </summary>
public class SettlementRow
{
    public long Id { get; set; }

    public Guid? HandResultId { get; set; }
    public HandResult? HandResult { get; set; }

    public Guid GameId { get; set; }

    public Guid PlayerId { get; set; }

    public int Seat { get; set; }

    /// <summary>Signed, in scoring units. Every set of rows for one event sums to zero.</summary>
    public int Delta { get; set; }

    [MaxLength(40)]
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
