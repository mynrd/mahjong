using System.ComponentModel.DataAnnotations;

namespace Mahjong.Api;

/// <summary>Claim a username. It is yours from here on if nobody got there first.</summary>
public sealed record RegisterRequest
{
    [Required, StringLength(24, MinimumLength = 3)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 8)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Sign in to an account that already exists.</summary>
public sealed record SignInRequest
{
    [Required, StringLength(24, MinimumLength = 1)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// What registering or signing in hands back. The token is the session: it is kept client-side and
/// sent on later calls, exactly as a seat token is, and it is what links a seat to the account when
/// a table is created or joined while signed in.
/// </summary>
public sealed record SignedInResponse(
    Guid UserId,
    string Username,
    string Token,
    DateTimeOffset ExpiresAt);

/// <summary>One finished hand this account had a seat in.</summary>
/// <param name="YourDelta">
/// What the hand cost or paid this player, in scoring units, summed over every settlement it wrote.
/// Ambitions are settled the moment they are declared, so a hand somebody lost can still have paid
/// them something.
/// </param>
/// <param name="CanReplay">
/// Whether the hand was recorded frame by frame. Hands played before replay frames existed have a
/// result but nothing to step through, and the profile says so rather than offering a dead link.
/// </param>
public sealed record PlayedGameView(
    string RoomCode,
    string RoomName,
    int HandNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int YourSeat,
    int? WinnerSeat,
    string? WinnerName,
    bool YouWon,
    string Reason,
    int TotalUnits,
    int YourDelta,
    bool CanReplay);

/// <summary>The numbers at the top of a profile, read off the hands below them.</summary>
public sealed record ProfileStatsView(
    int HandsPlayed,
    int HandsWon,
    int Tables,
    int NetUnits,
    int BestHandUnits);

/// <summary>A profile: who you are, and every hand of yours the server still holds.</summary>
public sealed record ProfileView(
    Guid UserId,
    string Username,
    DateTimeOffset CreatedAt,
    ProfileStatsView Stats,
    IReadOnlyList<PlayedGameView> Games);

/// <summary>
/// The summary line of a profile, worked out from the hands rather than kept as a running total on
/// the account.
///
/// Derived rather than stored on purpose. A counter on the row has to be kept correct by every
/// path that can end a hand - a win, an exhausted wall, an ambition paid mid-hand, a table the host
/// closed under a game - and the first one that forgets leaves a profile quietly lying. Reading it
/// off the hands cannot drift, and a night of mahjong is not enough rows for that to be slow.
/// </summary>
public static class ProfileStats
{
    public static ProfileStatsView Of(IReadOnlyList<PlayedGameView> games) =>
        new(
            games.Count,
            games.Count(g => g.YouWon),
            games.Select(g => g.RoomCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            games.Sum(g => g.YourDelta),
            // The best hand is the most one was ever worth to this player, so a night that went
            // badly still has a high point on it. Never negative: with nothing won it is 0.
            games.Count == 0 ? 0 : Math.Max(0, games.Max(g => g.YourDelta)));
}
