using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mahjong.Api;

public static class RoomEndpoints
{
    public const int SeatCount = 4;

    public static void MapRoomEndpoints(this WebApplication app)
    {
        var rooms = app.MapGroup("/api/rooms");

        rooms.MapPost("/", CreateRoom);
        rooms.MapPost("/{code}/join", JoinRoom);
        rooms.MapGet("/{code}", GetRoom);
        rooms.MapGet("/{code}/me", WhoAmI);
        rooms.MapPost("/{code}/bots", AddBots);
        rooms.MapPost("/{code}/start", StartHand);
    }

    // ------------------------------------------------------------------ create

    private static async Task<IResult> CreateRoom(
        CreateRoomRequest request,
        MahjongDbContext db,
        IConfiguration config,
        IOptions<RuleOptions> ruleDefaults,
        CancellationToken cancel)
    {
        if (request.Password.Length < 4)
            return Results.BadRequest(new ErrorResponse("PasswordTooShort", "Use at least 4 characters."));

        var name = request.Name.Trim();
        var displayName = request.DisplayName.Trim();

        if (name.Length == 0 || displayName.Length == 0)
            return Results.BadRequest(new ErrorResponse("NameRequired"));

        var (hash, salt, iterations) = PasswordHasher.Hash(request.Password);
        // A create request may carry its own house rules; otherwise the table takes the ones from
        // the Mahjong:Rules configuration section. Either way they are frozen into RulesJson, so a
        // later config change does not move the goalposts under a table that is already running.
        var rules = request.Rules ?? ruleDefaults.Value;

        var room = new Room
        {
            Code = await UniqueCodeAsync(db, cancel),
            Name = name,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            RulesJson = GameJson.Serialize(rules),
        };

        var token = PlayerToken.Issue();
        var host = new Player
        {
            RoomId = room.Id,
            DisplayName = displayName,
            Seat = 0,
            TokenHash = PlayerToken.HashOf(token),
        };

        room.Players.Add(host);
        room.HostPlayerId = host.Id;

        db.Rooms.Add(room);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(new SeatedResponse(
            room.Code,
            InviteUrl(config, room.Code),
            host.Id,
            host.Seat,
            token,
            IsHost: true));
    }

    // ------------------------------------------------------------------ join

    private static async Task<IResult> JoinRoom(
        string code,
        JoinRoomRequest request,
        MahjongDbContext db,
        IConfiguration config,
        CancellationToken cancel)
    {
        var normalised = RoomCode.Normalise(code);
        var displayName = request.DisplayName.Trim();

        if (displayName.Length == 0)
            return Results.BadRequest(new ErrorResponse("NameRequired"));

        var room = await db.Rooms
            .Include(r => r.Players)
            .FirstOrDefaultAsync(r => r.Code == normalised, cancel);

        if (room is null)
            return Results.NotFound(new ErrorResponse("RoomNotFound", $"No room with code {normalised}."));

        if (!PasswordHasher.Verify(request.Password, room.PasswordHash, room.PasswordSalt, room.PasswordIterations))
            return Results.Json(new ErrorResponse("WrongPassword"), statusCode: StatusCodes.Status401Unauthorized);

        if (room.Status == RoomStatus.Closed)
            return Results.Conflict(new ErrorResponse("RoomClosed"));

        // Seats are claimed optimistically. Two people tapping Join at the same moment both pick
        // the same free seat, and the unique index on (RoomId, Seat) makes one of them lose; that
        // one simply tries the next free seat. Checking first and then inserting would leave a
        // window where both succeed.
        for (var attempt = 0; attempt < SeatCount; attempt++)
        {
            var taken = await db.Players
                .Where(p => p.RoomId == room.Id)
                .Select(p => p.Seat)
                .ToListAsync(cancel);

            var free = Enumerable.Range(0, SeatCount).Except(taken).ToList();
            if (free.Count == 0)
                return Results.Conflict(new ErrorResponse("RoomFull", "All four seats are taken."));

            var token = PlayerToken.Issue();
            var player = new Player
            {
                RoomId = room.Id,
                DisplayName = displayName,
                Seat = free[0],
                TokenHash = PlayerToken.HashOf(token),
            };

            db.Players.Add(player);

            try
            {
                await db.SaveChangesAsync(cancel);

                return Results.Ok(new SeatedResponse(
                    room.Code,
                    InviteUrl(config, room.Code),
                    player.Id,
                    player.Seat,
                    token,
                    IsHost: false));
            }
            catch (DbUpdateException)
            {
                // Somebody else took that seat between the read and the insert. Drop the failed
                // entity and look again.
                db.Entry(player).State = EntityState.Detached;
            }
        }

        return Results.Conflict(new ErrorResponse("RoomFull", "All four seats were taken while joining."));
    }

    // ------------------------------------------------------------------ read

    private static async Task<IResult> GetRoom(
        string code,
        MahjongDbContext db,
        IConfiguration config,
        CancellationToken cancel)
    {
        var room = await LoadAsync(db, code, cancel);
        return room is null
            ? Results.NotFound(new ErrorResponse("RoomNotFound"))
            : Results.Ok(ToView(room, config));
    }

    /// <summary>
    /// Lets a client that already holds a token confirm which seat it belongs to. This is what
    /// makes a browser refresh land back in the same seat instead of asking to join again.
    /// </summary>
    private static async Task<IResult> WhoAmI(
        string code,
        HttpContext http,
        PlayerAuth auth,
        MahjongDbContext db,
        IConfiguration config,
        CancellationToken cancel)
    {
        var player = await auth.ResolveForRoomAsync(http, RoomCode.Normalise(code), cancel);
        if (player is null)
            return Results.Json(new ErrorResponse("NotSeated"), statusCode: StatusCodes.Status401Unauthorized);

        var room = await LoadAsync(db, code, cancel);
        if (room is null) return Results.NotFound(new ErrorResponse("RoomNotFound"));

        return Results.Ok(new
        {
            playerId = player.Id,
            seat = player.Seat,
            displayName = player.DisplayName,
            isHost = room.HostPlayerId == player.Id,
            room = ToView(room, config),
        });
    }

    // ------------------------------------------------------------------ bots

    private static async Task<IResult> AddBots(
        string code,
        AddBotsRequest request,
        HttpContext http,
        PlayerAuth auth,
        MahjongDbContext db,
        IConfiguration config,
        CancellationToken cancel)
    {
        var normalised = RoomCode.Normalise(code);

        var player = await auth.ResolveForRoomAsync(http, normalised, cancel);
        if (player is null)
            return Results.Json(new ErrorResponse("NotSeated"), statusCode: StatusCodes.Status401Unauthorized);

        var room = await LoadAsync(db, normalised, cancel);
        if (room is null) return Results.NotFound(new ErrorResponse("RoomNotFound"));

        if (room.HostPlayerId != player.Id)
            return Results.Json(new ErrorResponse("HostOnly", "Only the seat that made the room can add bots."),
                statusCode: StatusCodes.Status403Forbidden);

        if (room.Status != RoomStatus.Lobby)
            return Results.Conflict(new ErrorResponse("AlreadyPlaying"));

        var taken = room.Players.Select(p => p.Seat).ToHashSet();
        var free = Enumerable.Range(0, SeatCount).Where(s => !taken.Contains(s)).ToList();
        var wanted = Math.Min(request.Count ?? free.Count, free.Count);

        if (wanted == 0) return Results.Conflict(new ErrorResponse("NoFreeSeats"));

        var botNumber = room.Players.Count(p => p.IsBot);

        foreach (var seat in free.Take(wanted))
        {
            botNumber++;
            db.Players.Add(new Player
            {
                RoomId = room.Id,
                DisplayName = $"Bot {botNumber}",
                Seat = seat,
                IsBot = true,
                IsConnected = true,
                // Bots never authenticate, but the column is not nullable and a shared empty value
                // would collide on the token index, so they get a throwaway token like anyone else.
                TokenHash = PlayerToken.HashOf(PlayerToken.Issue()),
            });
        }

        await db.SaveChangesAsync(cancel);

        var refreshed = await LoadAsync(db, normalised, cancel);
        return Results.Ok(ToView(refreshed!, config));
    }

    // ------------------------------------------------------------------ start a hand

    private static async Task<IResult> StartHand(
        string code,
        HttpContext http,
        PlayerAuth auth,
        GameService games,
        CancellationToken cancel)
    {
        var normalised = RoomCode.Normalise(code);

        var player = await auth.ResolveForRoomAsync(http, normalised, cancel);
        if (player is null)
            return Results.Json(new ErrorResponse("NotSeated"), statusCode: StatusCodes.Status401Unauthorized);

        var result = await games.StartHandAsync(normalised, player.Id, cancel);

        return result.Success
            ? Results.Ok(new { started = true })
            : Results.Conflict(new ErrorResponse(result.Error!, result.Detail));
    }

    // ------------------------------------------------------------------ helpers

    private static Task<Room?> LoadAsync(MahjongDbContext db, string code, CancellationToken cancel) =>
        db.Rooms
            .Include(r => r.Players)
            .FirstOrDefaultAsync(r => r.Code == RoomCode.Normalise(code), cancel);

    private static async Task<string> UniqueCodeAsync(MahjongDbContext db, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = RoomCode.Generate();
            if (!await db.Rooms.AnyAsync(r => r.Code == candidate, cancel)) return candidate;
        }

        throw new InvalidOperationException("Could not find a free room code after 20 attempts.");
    }

    private static string InviteUrl(IConfiguration config, string code)
    {
        var baseUrl = config["Mahjong:WebBaseUrl"]?.TrimEnd('/') ?? "http://localhost:4200";
        return $"{baseUrl}/join/{code}";
    }

    private static RoomView ToView(Room room, IConfiguration config)
    {
        var bySeat = room.Players.ToDictionary(p => p.Seat);

        var seats = Enumerable.Range(0, SeatCount).Select(seat =>
        {
            if (!bySeat.TryGetValue(seat, out var player))
                return new SeatView(seat, null, false, false, false, 0);

            return new SeatView(
                seat,
                player.DisplayName,
                player.IsBot,
                player.IsConnected,
                room.HostPlayerId == player.Id,
                player.Balance);
        }).ToList();

        return new RoomView(
            room.Code,
            room.Name,
            (RoomStatusView)room.Status,
            InviteUrl(config, room.Code),
            room.HandsPlayed,
            seats,
            GameJson.Deserialize<RuleOptions>(room.RulesJson));
    }
}
