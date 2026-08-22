using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Api;

/// <summary>
/// The live connection for a seat. Everything that happens during a hand goes over this rather
/// than REST, because the server has to push: a player needs to know it is their turn without
/// asking, and a claim window is only a few seconds wide.
///
/// A connection is bound to a seat once, at connect time, from the bearer token. Hub methods
/// never take a seat number from the caller, so a client cannot act on somebody else's behalf by
/// sending a different number.
/// </summary>
public sealed class GameHub(
    PlayerAuth auth,
    RoomRegistry registry,
    GameService games,
    MahjongDbContext db,
    ILogger<GameHub> logger) : Hub
{
    private const string SeatKey = "seat";
    private const string RoomKey = "room";
    private const string PlayerKey = "player";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var token = http is null ? null : PlayerAuth.TokenFrom(http);

        var player = await auth.ResolveAsync(token);
        if (player?.Room is null)
        {
            logger.LogWarning("Rejecting hub connection {Connection}: no seat for that token.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var code = player.Room.Code;

        Context.Items[SeatKey] = player.Seat;
        Context.Items[RoomKey] = code;
        Context.Items[PlayerKey] = player.Id;

        var session = await games.RestoreAsync(code) ?? registry.GetOrCreate(player.RoomId, code);
        session.Attach(player.Seat, Context.ConnectionId);

        player.IsConnected = true;
        player.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(code));

        // A reconnecting client needs the current picture immediately, not at the next move.
        var view = await games.ViewForAsync(code, player.Seat);
        if (view is not null) await Clients.Caller.SendAsync("StateChanged", view);

        await Clients.Group(GroupFor(code)).SendAsync("SeatConnected", player.Seat, player.DisplayName);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RoomKey, out var codeObj) && codeObj is string code
            && Context.Items.TryGetValue(SeatKey, out var seatObj) && seatObj is int seat)
        {
            var session = registry.Find(code);
            var lastOne = session?.Detach(seat, Context.ConnectionId) ?? true;

            if (lastOne && Context.Items.TryGetValue(PlayerKey, out var idObj) && idObj is Guid playerId)
            {
                // Losing the connection marks the seat away. It never frees the seat: the player is
                // mid-hand and holds tiles, and handing the seat to somebody else would be worse
                // than waiting for them to come back.
                var player = await db.Players.FindAsync(playerId);
                if (player is not null)
                {
                    player.IsConnected = false;
                    player.LastSeenAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                }
            }

            await Clients.Group(GroupFor(code)).SendAsync("SeatDisconnected", seat);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ------------------------------------------------------------------ moves

    public Task<Result> Draw() => Move(new GameMove.Draw());

    public Task<Result> Discard(int tileId) => Move(new GameMove.Discard(tileId));

    public Task<Result> Claim(ClaimKind kind, int[]? tileIds) => Move(new GameMove.Claim(kind, tileIds ?? []));

    public Task<Result> Pass() => Move(new GameMove.Pass());

    public Task<Result> DeclareSecretKang(string face) => Move(new GameMove.SecretKang(face));

    public Task<Result> DeclareSagasa(string face) => Move(new GameMove.Sagasa(face));

    public Task<Result> DeclareTodas() => Move(new GameMove.Todas());

    private async Task<Result> Move(GameMove move)
    {
        if (Context.Items[RoomKey] is not string code || Context.Items[SeatKey] is not int seat)
            return Result.Fail("NotSeated");

        var result = await games.MoveAsync(code, seat, move);

        if (!result.Success)
            logger.LogDebug("Seat {Seat} in {Code} tried {Move}: {Error} {Detail}",
                seat, code, move.GetType().Name, result.Error, result.Detail);

        return result;
    }

    public static string GroupFor(string code) => $"room:{code.ToUpperInvariant()}";
}
