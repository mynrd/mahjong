using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// Registering an account, signing in to it, and turning the token that comes back into the
/// account it belongs to.
///
/// The third of three ways this server knows who is calling, and the only one that outlives a
/// single table. <see cref="PlayerAuth"/> proves you are holding a seat and <see cref="ReplayAuth"/>
/// proves you knew a room password; both are scoped to one room and neither remembers you
/// afterwards. An account is the thing a night's hands can be gathered under, so it gets its own
/// token, its own table of sessions, and a lifetime measured in weeks rather than hours.
///
/// It grants nothing on its own. Signing in never seats you anywhere: taking a seat is still the
/// join endpoint, and the seat token it hands back is still what plays the hand.
/// </summary>
public sealed class UserAuth(MahjongDbContext db, TimeProvider clock)
{
    /// <summary>
    /// How long a sign-in lasts. Long enough that the phone somebody plays on stays signed in
    /// between games, short enough that a token left on a borrowed laptop stops working.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Why a registration was refused, or null when it went through.</summary>
    public enum RegisterError
    {
        None,
        BadUsername,
        WeakPassword,
        UsernameTaken,
    }

    public sealed record Registration(RegisterError Error, UserAccount? User, SignedInResponse? Response)
    {
        public bool Success => Error == RegisterError.None;
    }

    // ------------------------------------------------------------------ register

    /// <summary>
    /// Claims a username, if it is still free. First come first served, and the race is settled by
    /// the unique index rather than by the check below: two registrations of the same name in the
    /// same moment both find it free, and the loser lands here as a
    /// <see cref="RegisterError.UsernameTaken"/> from the failed insert instead of as a second
    /// account with the same name.
    /// </summary>
    public async Task<Registration> RegisterAsync(string username, string password, CancellationToken cancel = default)
    {
        var trimmed = username.Trim();

        if (!UserName.IsWellFormed(trimmed))
            return new Registration(RegisterError.BadUsername, null, null);

        if (password.Length is < UserName.MinPasswordLength or > UserName.MaxPasswordLength)
            return new Registration(RegisterError.WeakPassword, null, null);

        var key = UserName.KeyOf(trimmed);

        if (await db.Users.AnyAsync(u => u.UsernameKey == key, cancel))
            return new Registration(RegisterError.UsernameTaken, null, null);

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        var now = clock.GetUtcNow();

        var user = new UserAccount
        {
            Username = trimmed,
            UsernameKey = key,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            CreatedAt = now,
            LastSignedInAt = now,
        };

        var token = NewSession(user, now);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Somebody else took the name between the read above and this insert. The index is
            // what actually decides it, so this is the only answer that can be given.
            return new Registration(RegisterError.UsernameTaken, null, null);
        }

        return new Registration(RegisterError.None, user, Response(user, token, now));
    }

    // ------------------------------------------------------------------ sign in

    /// <summary>Checks a password and hands back a token, or null when the pair does not match.</summary>
    public async Task<SignedInResponse?> SignInAsync(string username, string password, CancellationToken cancel = default)
    {
        var key = UserName.KeyOf(username);
        var user = await db.Users.FirstOrDefaultAsync(u => u.UsernameKey == key, cancel);

        // A wrong name and a wrong password are told apart by the caller as one answer on purpose:
        // saying which of the two was wrong is a way to find out which names exist.
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
            return null;

        var now = clock.GetUtcNow();
        var token = NewSession(user, now);
        user.LastSignedInAt = now;

        // Expired rows for this account go at the same time. There is no background job on a
        // machine that gets switched off at the end of the night, so the cleanup rides along with
        // the write that would otherwise let them pile up.
        await db.UserSessions
            .Where(s => s.UserId == user.Id && s.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancel);

        await db.SaveChangesAsync(cancel);

        return Response(user, token, now);
    }

    /// <summary>Drops the session this request is holding. Other browsers stay signed in.</summary>
    public async Task SignOutAsync(HttpContext http, CancellationToken cancel = default)
    {
        var token = PlayerAuth.TokenFrom(http);
        if (string.IsNullOrWhiteSpace(token)) return;

        var hash = PlayerToken.HashOf(token);
        await db.UserSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(cancel);
    }

    // ------------------------------------------------------------------ resolve

    /// <summary>The account this request is signed in as, or null when it is signed in as nobody.</summary>
    public async Task<UserAccount?> ResolveAsync(HttpContext http, CancellationToken cancel = default) =>
        await ResolveAsync(PlayerAuth.TokenFrom(http), cancel);

    public async Task<UserAccount?> ResolveAsync(string? token, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = PlayerToken.HashOf(token);

        var session = await db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, cancel);

        if (session?.User is null) return null;

        return session.ExpiresAt > clock.GetUtcNow() ? session.User : null;
    }

    // ------------------------------------------------------------------ helpers

    private string NewSession(UserAccount user, DateTimeOffset now)
    {
        var token = PlayerToken.Issue();

        var session = new UserSession
        {
            TokenHash = PlayerToken.HashOf(token),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };

        // Added through the set, not only through the navigation. UserSession.Id is filled in by
        // its property initializer, and an entity EF first meets through the collection of an
        // account it is already tracking is taken for a row that exists whenever its key is
        // already set. Sign-in then saved the brand new session as
        // UPDATE UserSessions ... WHERE Id = <a row nobody has ever inserted>, which matched
        // nothing and came back as a concurrency failure - every sign-in, not a race.
        // Registering was fine only because db.Users.Add walks the graph and marks the session
        // added along with the account.
        user.Sessions.Add(session);
        db.UserSessions.Add(session);

        return token;
    }

    private static SignedInResponse Response(UserAccount user, string token, DateTimeOffset now) =>
        new(user.Id, user.Username, token, now + Lifetime);
}
