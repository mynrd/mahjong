using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// Resolves the bearer token a player was handed at join time back into their seat.
///
/// This is the whole authentication story: there are no accounts, so possession of the token is
/// the claim. It is looked up by hash on every call rather than being a self-contained signed
/// token, which costs one indexed read but means a seat can be revoked immediately and no signing
/// key has to be managed or rotated on a machine sitting on someone's desk.
/// </summary>
public sealed class PlayerAuth(MahjongDbContext db)
{
    public const string Scheme = "Bearer ";

    /// <summary>Pulls the token out of the Authorization header, or the SignalR query string.</summary>
    public static string? TokenFrom(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return header[Scheme.Length..].Trim();

        // Browsers cannot set headers on a WebSocket handshake, so SignalR passes the token as a
        // query parameter instead. Same token, same lookup.
        return http.Request.Query["access_token"].FirstOrDefault();
    }

    public async Task<Player?> ResolveAsync(string? token, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = PlayerToken.HashOf(token);

        return await db.Players
            .Include(p => p.Room)
            .FirstOrDefaultAsync(p => p.TokenHash == hash, cancel);
    }

    public Task<Player?> ResolveAsync(HttpContext http, CancellationToken cancel = default) =>
        ResolveAsync(TokenFrom(http), cancel);

    /// <summary>Resolves the caller and checks they belong to the room in the route.</summary>
    public async Task<Player?> ResolveForRoomAsync(HttpContext http, string roomCode, CancellationToken cancel = default)
    {
        var player = await ResolveAsync(http, cancel);
        if (player?.Room is null) return null;

        return string.Equals(player.Room.Code, roomCode, StringComparison.OrdinalIgnoreCase) ? player : null;
    }
}
