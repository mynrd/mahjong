using Mahjong.Infrastructure;

namespace Mahjong.Api.Tests;

/// <summary>
/// The two rules that make an account mean anything: which names may be claimed, and when two of
/// them count as the same name. The second is the one that matters - a username is first come,
/// first served, and the unique index that enforces that is on the key
/// <see cref="UserName.KeyOf"/> produces, so anything it fails to fold apart is a second account
/// able to sit at a table under a name somebody else registered.
/// </summary>
public class UserNameTests
{
    [Theory]
    [InlineData("mynard")]
    [InlineData("Tito.Ben")]
    [InlineData("kuya_jun")]
    [InlineData("ate-rose")]
    [InlineData("a1b")]
    [InlineData("012345678901234567890123")]
    public void Ordinary_names_are_accepted(string username) =>
        Assert.True(UserName.IsWellFormed(username));

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("0123456789012345678901234")]
    [InlineData("has space")]
    [InlineData("drop;table")]
    [InlineData("emoji\U0001F600")]
    // Latin-looking Cyrillic. Two players told apart only by what is on the screen is exactly what
    // the narrow alphabet is there to prevent.
    [InlineData("mynаrd")]
    public void Names_that_would_cause_trouble_are_refused(string username) =>
        Assert.False(UserName.IsWellFormed(username));

    [Fact]
    public void Surrounding_space_does_not_make_a_new_name()
    {
        Assert.True(UserName.IsWellFormed("  mynard  "));
        Assert.Equal("mynard", UserName.KeyOf("  mynard  "));
    }

    [Fact]
    public void Case_is_folded_away_so_one_registration_takes_all_of_them()
    {
        var key = UserName.KeyOf("Mynard");

        Assert.Equal(key, UserName.KeyOf("mynard"));
        Assert.Equal(key, UserName.KeyOf("MYNARD"));
        Assert.Equal(key, UserName.KeyOf("mYnArD"));
    }

    [Fact]
    public void Different_names_keep_different_keys()
    {
        Assert.NotEqual(UserName.KeyOf("mynard"), UserName.KeyOf("mynard1"));
        Assert.NotEqual(UserName.KeyOf("tito.ben"), UserName.KeyOf("tito_ben"));
    }

    /// <summary>
    /// An account password is checked the same way a room password is, and the salt is what stops
    /// two people who picked the same one from having the same row.
    /// </summary>
    [Fact]
    public void A_password_verifies_against_its_own_hash_and_nothing_else()
    {
        var (hash, salt, iterations) = PasswordHasher.Hash("correct horse");

        Assert.True(PasswordHasher.Verify("correct horse", hash, salt, iterations));
        Assert.False(PasswordHasher.Verify("Correct horse", hash, salt, iterations));
        Assert.False(PasswordHasher.Verify("", hash, salt, iterations));

        var other = PasswordHasher.Hash("correct horse");
        Assert.NotEqual(hash, other.Hash);
    }
}

/// <summary>
/// The summary line of a profile. It is read off the hands rather than kept as a running total, so
/// these are the tests that say what "read off the hands" means when the hands disagree with the
/// obvious answer - a hand somebody lost that still paid them an ambition, or a hand at a table
/// they played twice.
/// </summary>
public class ProfileStatsTests
{
    private static PlayedGameView Game(
        string roomCode = "ABC234",
        int handNumber = 1,
        bool youWon = false,
        int yourDelta = 0) =>
        new(
            roomCode,
            "Sunday game",
            handNumber,
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 12, 30, 0, TimeSpan.Zero),
            YourSeat: 0,
            WinnerSeat: youWon ? 0 : 1,
            WinnerName: youWon ? "Mynard" : "Tito Ben",
            youWon,
            Reason: "Todas",
            TotalUnits: 12,
            yourDelta,
            CanReplay: true);

    [Fact]
    public void An_account_that_has_played_nothing_reads_as_zeroes()
    {
        var stats = ProfileStats.Of([]);

        Assert.Equal(0, stats.HandsPlayed);
        Assert.Equal(0, stats.HandsWon);
        Assert.Equal(0, stats.Tables);
        Assert.Equal(0, stats.NetUnits);
        Assert.Equal(0, stats.BestHandUnits);
    }

    [Fact]
    public void Hands_wins_and_the_running_total_come_off_the_list()
    {
        var stats = ProfileStats.Of([
            Game(handNumber: 1, youWon: true, yourDelta: 24),
            Game(handNumber: 2, yourDelta: -8),
            Game(handNumber: 3, yourDelta: -6),
        ]);

        Assert.Equal(3, stats.HandsPlayed);
        Assert.Equal(1, stats.HandsWon);
        Assert.Equal(10, stats.NetUnits);
        Assert.Equal(24, stats.BestHandUnits);
    }

    /// <summary>
    /// Hands are counted per hand and tables per table: three hands at one table is one table, and
    /// counting rows would turn a single evening into three of them.
    /// </summary>
    [Fact]
    public void Several_hands_at_one_table_are_still_one_table()
    {
        var stats = ProfileStats.Of([
            Game(roomCode: "ABC234", handNumber: 1),
            Game(roomCode: "ABC234", handNumber: 2),
            Game(roomCode: "XYZ789", handNumber: 1),
        ]);

        Assert.Equal(3, stats.HandsPlayed);
        Assert.Equal(2, stats.Tables);
    }

    /// <summary>Room codes are matched the way the server matches them, so case cannot split a table.</summary>
    [Fact]
    public void The_same_table_typed_two_ways_is_one_table() =>
        Assert.Equal(1, ProfileStats.Of([Game(roomCode: "ABC234"), Game(roomCode: "abc234")]).Tables);

    /// <summary>
    /// A night that only went down still has a best hand of 0 rather than the least bad loss.
    /// "Best hand" is what a hand was ever worth, and nothing was ever worth less than nothing.
    /// </summary>
    [Fact]
    public void A_losing_night_has_no_best_hand_rather_than_a_negative_one()
    {
        var stats = ProfileStats.Of([Game(yourDelta: -8), Game(handNumber: 2, yourDelta: -3)]);

        Assert.Equal(-11, stats.NetUnits);
        Assert.Equal(0, stats.BestHandUnits);
    }

    /// <summary>
    /// Ambitions are settled the moment they are declared, so a hand somebody lost can still have
    /// paid them. The list carries what the hand was worth to this player, not who won it.
    /// </summary>
    [Fact]
    public void A_hand_that_was_lost_can_still_have_paid()
    {
        var stats = ProfileStats.Of([Game(youWon: false, yourDelta: 3)]);

        Assert.Equal(0, stats.HandsWon);
        Assert.Equal(3, stats.NetUnits);
        Assert.Equal(3, stats.BestHandUnits);
    }
}
