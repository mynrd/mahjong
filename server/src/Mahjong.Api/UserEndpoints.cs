using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// Accounts, and the profile built out of the hands one has played.
///
/// Nothing here is required to play: a table with nobody signed in works exactly as it did. What an
/// account buys is that the hands stop being scattered across rooms whose codes you have to
/// remember - the seats you took while signed in carry your account id, and this is where they are
/// gathered back up.
/// </summary>
public static class UserEndpoints
{
    /// <summary>How many hands a profile carries. A night is far fewer; this is the guard rail.</summary>
    public const int GameLimit = 200;

    public static void MapUserEndpoints(this WebApplication app)
    {
        var users = app.MapGroup("/api/users");

        users.MapPost("/register", Register);
        users.MapPost("/login", SignIn);
        users.MapPost("/logout", SignOut);
        users.MapGet("/me", Profile);
        users.MapGet("/me/games", Games);
    }

    // ------------------------------------------------------------------ register

    private static async Task<IResult> Register(
        RegisterRequest request,
        UserAuth auth,
        CancellationToken cancel)
    {
        var registration = await auth.RegisterAsync(request.Username, request.Password, cancel);

        return registration.Error switch
        {
            UserAuth.RegisterError.None => Results.Ok(registration.Response),

            UserAuth.RegisterError.BadUsername => Results.BadRequest(new ErrorResponse(
                "BadUsername",
                $"Between {UserName.MinLength} and {UserName.MaxLength} characters: letters, numbers, and . _ - only.")),

            UserAuth.RegisterError.WeakPassword => Results.BadRequest(new ErrorResponse(
                "WeakPassword",
                $"Use at least {UserName.MinPasswordLength} characters.")),

            // First come first served, and somebody came first.
            _ => Results.Conflict(new ErrorResponse(
                "UsernameTaken",
                $"{request.Username.Trim()} is already registered. Pick another name.")),
        };
    }

    // ------------------------------------------------------------------ sign in and out

    private static async Task<IResult> SignIn(
        SignInRequest request,
        UserAuth auth,
        CancellationToken cancel)
    {
        var signedIn = await auth.SignInAsync(request.Username, request.Password, cancel);

        // One answer for a name that does not exist and for a password that does not match. Two
        // answers would be a way to find out which usernames are taken without registering.
        return signedIn is null
            ? Results.Json(new ErrorResponse("BadCredentials", "That username and password do not match."),
                statusCode: StatusCodes.Status401Unauthorized)
            : Results.Ok(signedIn);
    }

    private static async Task<IResult> SignOut(HttpContext http, UserAuth auth, CancellationToken cancel)
    {
        await auth.SignOutAsync(http, cancel);
        return Results.Ok(new { signedOut = true });
    }

    // ------------------------------------------------------------------ profile

    private static async Task<IResult> Profile(
        HttpContext http,
        UserAuth auth,
        MahjongDbContext db,
        CancellationToken cancel)
    {
        var user = await auth.ResolveAsync(http, cancel);
        if (user is null) return NotSignedIn();

        var games = await GamesForAsync(db, user, cancel);

        return Results.Ok(new ProfileView(
            user.Id,
            user.Username,
            user.CreatedAt,
            ProfileStats.Of(games),
            games));
    }

    /// <summary>The hands on their own, for a client that already knows who it is signed in as.</summary>
    private static async Task<IResult> Games(
        HttpContext http,
        UserAuth auth,
        MahjongDbContext db,
        CancellationToken cancel)
    {
        var user = await auth.ResolveAsync(http, cancel);
        if (user is null) return NotSignedIn();

        return Results.Ok(await GamesForAsync(db, user, cancel));
    }

    // ------------------------------------------------------------------ the game list

    /// <summary>
    /// Every finished hand this account had a seat in, newest first.
    ///
    /// Only <see cref="GameStatus.Finished"/> hands are in it, for the same reason the replay list
    /// only holds those: a hand still being played would be a way to read the other three hands at
    /// a table you are sitting at, and a hand the host closed under never reached a result to show.
    /// </summary>
    private static async Task<List<PlayedGameView>> GamesForAsync(
        MahjongDbContext db,
        UserAccount user,
        CancellationToken cancel)
    {
        // One row per table this account ever sat at. The seat number is per room, so it is carried
        // along rather than looked up again per hand.
        var seats = await db.Players
            .Where(p => p.UserId == user.Id)
            .Select(p => new
            {
                PlayerId = p.Id,
                p.RoomId,
                p.Seat,
                RoomCode = p.Room!.Code,
                RoomName = p.Room!.Name,
            })
            .ToListAsync(cancel);

        if (seats.Count == 0) return [];

        // Grouped rather than keyed straight off, so a room that somehow holds two rows for this
        // account - two joins landing in the same instant, either side of the check that stops it -
        // is one entry here rather than an exception that takes the whole profile down.
        var seatByRoom = seats
            .GroupBy(s => s.RoomId)
            .ToDictionary(group => group.Key, group => group.First());
        var roomIds = seats.Select(s => s.RoomId).ToList();
        var playerIds = seats.Select(s => s.PlayerId).ToList();

        var rows = await db.Games
            .Where(g => roomIds.Contains(g.RoomId) && g.Status == GameStatus.Finished)
            .OrderByDescending(g => g.EndedAt ?? g.StartedAt)
            .Take(GameLimit)
            .Select(g => new { g.Id, g.RoomId, g.HandNumber, g.StartedAt, g.EndedAt })
            .ToListAsync(cancel);

        if (rows.Count == 0) return [];

        var gameIds = rows.Select(g => g.Id).ToList();

        // Flat reads and then a stitch, rather than one query carrying a subquery per column. The
        // answer is the same shape and it stays well inside what the provider can translate, which
        // is worth more here than saving a few round trips over at most a couple of hundred rows.
        var results = await db.HandResults
            .Where(r => gameIds.Contains(r.GameId))
            .Select(r => new { r.GameId, r.WinnerSeat, r.Reason, r.TotalUnits })
            .ToListAsync(cancel);

        var resultByGame = results.ToDictionary(r => r.GameId);

        // A hand played before frames were recorded has a result and nothing to step through, so
        // the profile can say so instead of offering a link to an empty replay.
        var recorded = await db.GameFrames
            .Where(f => gameIds.Contains(f.GameId))
            .Select(f => f.GameId)
            .Distinct()
            .ToListAsync(cancel);

        var recordedGames = recorded.ToHashSet();

        // Summed rather than taken from one row: an ambition declared mid-hand writes its own
        // settlement, so a hand can have paid this seat more than once - and a hand that paid it
        // nothing has no row at all, which is why the lookup below falls back to zero.
        var deltas = await db.Settlements
            .Where(s => gameIds.Contains(s.GameId) && playerIds.Contains(s.PlayerId))
            .GroupBy(s => s.GameId)
            .Select(group => new { GameId = group.Key, Delta = group.Sum(s => s.Delta) })
            .ToListAsync(cancel);

        var deltaByGame = deltas.ToDictionary(d => d.GameId, d => d.Delta);

        // Who was in which seat, per room, so the winner has a name and not just a number.
        var names = await db.Players
            .Where(p => roomIds.Contains(p.RoomId))
            .Select(p => new { p.RoomId, p.Seat, p.DisplayName })
            .ToListAsync(cancel);

        var nameBySeat = names.ToDictionary(n => (n.RoomId, n.Seat), n => n.DisplayName);

        return rows.Select(row =>
        {
            var seat = seatByRoom[row.RoomId];
            var result = resultByGame.GetValueOrDefault(row.Id);
            var winnerSeat = result?.WinnerSeat;

            return new PlayedGameView(
                seat.RoomCode,
                seat.RoomName,
                row.HandNumber,
                row.StartedAt,
                row.EndedAt,
                seat.Seat,
                winnerSeat,
                winnerSeat is { } won && nameBySeat.TryGetValue((row.RoomId, won), out var name) ? name : null,
                winnerSeat == seat.Seat,
                result?.Reason ?? nameof(HandEndReason.WallExhausted),
                result?.TotalUnits ?? 0,
                deltaByGame.GetValueOrDefault(row.Id),
                recordedGames.Contains(row.Id));
        }).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static IResult NotSignedIn() =>
        Results.Json(
            new ErrorResponse("NotSignedIn", "Sign in to see your games."),
            statusCode: StatusCodes.Status401Unauthorized);
}
