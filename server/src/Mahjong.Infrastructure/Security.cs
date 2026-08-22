using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Infrastructure;

/// <summary>
/// Password handling for room passwords. There are no user accounts, but a room password still
/// guards who can take a seat, so it gets the same treatment a real password would: a per-room
/// random salt, a slow hash, and a comparison that does not leak timing.
/// </summary>
public static class PasswordHasher
{
    /// <summary>
    /// PBKDF2 iterations. Deliberately high: a room password is short and typed by hand, so the
    /// cost of guessing has to come from the hash. Stored per room so it can be raised later
    /// without invalidating rooms hashed at the old cost.
    /// </summary>
    public const int Iterations = 210_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (byte[] Hash, byte[] Salt, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);
        return (hash, salt, Iterations);
    }

    public static bool Verify(string password, byte[] expectedHash, byte[] salt, int iterations)
    {
        var actual = Derive(password, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
}

/// <summary>
/// The bearer token a player gets when they take a seat. It is the only thing identifying them
/// on later requests, so it is generated from a cryptographic source and only its hash is stored:
/// a leaked database still does not let anyone impersonate a seat.
/// </summary>
public static class PlayerToken
{
    private const int TokenBytes = 32;

    public static string Issue() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Room codes for invite links. The alphabet leaves out characters that get misread when someone
/// reads a code out loud or copies it off a phone screen: no O against 0, no I or l against 1.
/// </summary>
public static class RoomCode
{
    public const int Length = 6;

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var code = new char[Length];
        for (var i = 0; i < Length; i++)
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(code);
    }

    /// <summary>Normalises user input, so a code typed in lower case or with spaces still matches.</summary>
    public static string Normalise(string input) =>
        new(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsWellFormed(string code) =>
        code.Length == Length && code.All(Alphabet.Contains);
}
