using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// Resolves the bearer token handed out when somebody typed a room's password, back into the room
/// it lets them read.
///
/// Separate from <see cref="PlayerAuth"/> on purpose. A seat token proves you are playing; this
/// proves only that you know the password, which is all a replay needs and all it should grant. A
/// replay link is opened from a browser that usually never took a seat, so there is no seat token
/// to present, and issuing a real seat would be far too much authority for reading old hands.
/// </summary>
public sealed class ReplayAuth(MahjongDbContext db, TimeProvider clock, UserAuth users)
{
    /// <summary>
    /// How long an unlock lasts. Long enough to sit and go through a night's hands, short enough
    /// that a token left in a browser is not permanent access to a room whose password changed.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>Checks the password and hands back a token, or null when it is wrong.</summary>
    public async Task<ReplayUnlockResponse?> UnlockAsync(Room room, string password, CancellationToken cancel = default)
    {
        if (!PasswordHasher.Verify(password, room.PasswordHash, room.PasswordSalt, room.PasswordIterations))
            return null;

        var token = PlayerToken.Issue();
        var now = clock.GetUtcNow();

        db.ReplayTokens.Add(new ReplayToken
        {
            RoomId = room.Id,
            TokenHash = PlayerToken.HashOf(token),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });

        // Expired rows for this room go at the same time. There is no background job on a machine
        // that gets switched off at the end of the night, so the cleanup rides along with the write
        // that would otherwise let them pile up.
        await db.ReplayTokens
            .Where(t => t.RoomId == room.Id && t.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancel);

        await db.SaveChangesAsync(cancel);

        return new ReplayUnlockResponse(token, now + Lifetime);
    }

    /// <summary>The room this request may read replays for, or null when it may not read any.</summary>
    public async Task<Room?> ResolveForRoomAsync(HttpContext http, string roomCode, CancellationToken cancel = default)
    {
        var token = PlayerAuth.TokenFrom(http);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = PlayerToken.HashOf(token);

        var granted = await db.ReplayTokens
            .Include(t => t.Room)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancel);

        if (granted?.Room is not null &&
            granted.ExpiresAt > clock.GetUtcNow() &&
            // A token for one room must not open another. Without this the check would be "did you
            // know *a* password", not "did you know *this* one".
            string.Equals(granted.Room.Code, roomCode, StringComparison.OrdinalIgnoreCase))
        {
            return granted.Room;
        }

        return await SeatedAccountRoomAsync(token, roomCode, cancel);
    }

    /// <summary>
    /// The other way in: a signed-in account that actually sat at this table.
    ///
    /// Somebody who played a hand should not have to remember the table password to look their own
    /// hand up weeks later - that is most of what a profile is for. It grants no more than the
    /// password does, and less broadly: it opens exactly the rooms this account has a seat in, and
    /// a room it never played stays shut whatever it is holding.
    /// </summary>
    private async Task<Room?> SeatedAccountRoomAsync(string token, string roomCode, CancellationToken cancel)
    {
        var account = await users.ResolveAsync(token, cancel);
        if (account is null) return null;

        var normalised = RoomCode.Normalise(roomCode);

        return await db.Players
            .Where(p => p.UserId == account.Id && p.Room!.Code == normalised)
            .Select(p => p.Room!)
            .FirstOrDefaultAsync(cancel);
    }
}
