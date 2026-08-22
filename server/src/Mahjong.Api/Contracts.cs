using System.ComponentModel.DataAnnotations;
using Mahjong.Domain;

namespace Mahjong.Api;

/// <summary>Create a table and take seat 0.</summary>
public sealed record CreateRoomRequest
{
    [Required, StringLength(60, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Shared with the other three players so they can sit down. Not a user password.</summary>
    [Required, StringLength(64, MinimumLength = 4)]
    public string Password { get; init; } = string.Empty;

    [Required, StringLength(24, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>House rules. Null uses the defaults from RULES.md.</summary>
    public RuleOptions? Rules { get; init; }
}

/// <summary>Take one of the free seats at an existing table.</summary>
public sealed record JoinRoomRequest
{
    [Required, StringLength(24, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// What a player gets back after creating or joining. The token is the whole session: it is
/// stored client-side and replayed to reattach to the same seat after a refresh.
/// </summary>
public sealed record SeatedResponse(
    string RoomCode,
    string InviteUrl,
    Guid PlayerId,
    int Seat,
    string PlayerToken,
    bool IsHost);

/// <summary>One seat as shown in the lobby.</summary>
public sealed record SeatView(
    int Seat,
    string? DisplayName,
    bool IsBot,
    bool IsConnected,
    bool IsHost,
    int Balance);

/// <summary>The lobby. Deliberately contains nothing about anybody's tiles.</summary>
public sealed record RoomView(
    string Code,
    string Name,
    RoomStatusView Status,
    string InviteUrl,
    int HandsPlayed,
    IReadOnlyList<SeatView> Seats,
    RuleOptions Rules)
{
    public int TakenSeats => Seats.Count(s => s.DisplayName is not null);
    public bool CanStart => TakenSeats == 4;
}

public enum RoomStatusView
{
    Lobby,
    Playing,
    Closed,
}

/// <summary>Ask the server to sit bots in whatever seats are still empty.</summary>
public sealed record AddBotsRequest
{
    /// <summary>How many bots to add. Null fills every free seat.</summary>
    [Range(1, 3)]
    public int? Count { get; init; }
}

public sealed record ErrorResponse(string Error, string? Detail = null);
