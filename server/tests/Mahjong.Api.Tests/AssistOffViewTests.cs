using Mahjong.Api;
using Mahjong.Domain;

namespace Mahjong.Api.Tests;

/// <summary>
/// With assist off, what the server withholds is the whole feature. Every hint the table draws -
/// the option rows, the coloured outlines on your own tiles, the badge letters, Auto Arrange - is
/// derived from two fields on the view. If either one keeps arriving, the setting does nothing.
///
/// The other half is subtler and matters just as much: the prompt has to go to all three seats. A
/// prompt that only appeared for the seats holding a pair would be the server saying "you can take
/// this", which is the one sentence assist off exists to stop it saying.
/// </summary>
public class AssistOffViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> Seats = new()
    {
        [0] = ("Mynard", false, true, 0),
        [1] = ("Tito Ben", false, true, 0),
        [2] = ("Ate Rose", false, true, 0),
        [3] = ("Kuya Jun", false, true, 0),
    };

    private static RuleOptions Manual => RuleOptions.Default with { JokerEnabled = false, AssistEnabled = false };

    private static RuleOptions Assisted => RuleOptions.Default with { JokerEnabled = false };

    /// <summary>
    /// An open window on a 5 chars thrown by seat 0. Seat 1 sits on its left and holds the run, so
    /// it can chow; seat 3 holds the pair, so it can pung; seat 2 holds two dots it can do nothing
    /// with. Every tile comes out of one pool, so no two seats hold the same physical copy.
    /// </summary>
    private sealed record Table(GameState State, IReadOnlyList<TileRef> Tiles)
    {
        public int[] PungTiles => [Tiles[3].Id, Tiles[4].Id];

        public ClaimPromptView? ClaimFor(int seat) =>
            GameViewBuilder.Build(State, "ABC123", seat, Seats).Claim;
    }

    private static Table Window(RuleOptions? rules = null)
    {
        var tiles = TileNotation.ParseRefs("5c 46c 55c 19d");

        var state = new GameState
        {
            Rules = rules ?? Manual,
            HandNumber = 1,
            ManoSeat = 0,
            Seed = 1,
            Wall = TileSet.All.Where(t => tiles.All(r => r.Id != t.Id)).ToList(),
            FrontIndex = 0,
            CurrentSeat = 0,
            Phase = GamePhase.AwaitingDiscard,
        };

        state.BackIndex = state.Wall.Count - 1;

        state.Hands[0].Concealed.Add(tiles[0]);                 // C5, about to be thrown
        state.Hands[1].Concealed.AddRange(tiles.Skip(1).Take(2));   // C4 C6
        state.Hands[3].Concealed.AddRange(tiles.Skip(3).Take(2));   // C5 C5
        state.Hands[2].Concealed.AddRange(tiles.Skip(5).Take(2));   // D1 D9

        MahjongGame.Discard(state, 0, tiles[0].Id, Now);

        return new Table(state, tiles);
    }

    [Fact]
    public void The_view_says_the_table_is_unassisted()
    {
        Assert.False(GameViewBuilder.Build(Window().State, "ABC123", 1, Seats).Assisted);
    }

    [Fact]
    public void No_seat_is_told_what_it_could_make_with_the_discard()
    {
        var table = Window();

        foreach (var seat in new[] { 1, 2, 3 })
        {
            var claim = table.ClaimFor(seat);
            Assert.NotNull(claim);
            Assert.Empty(claim!.Candidates);
            Assert.Empty(claim.YourOptions);
        }
    }

    [Fact]
    public void The_prompt_reaches_the_seat_that_can_do_nothing_with_the_tile()
    {
        var table = Window();

        // Seat 2 holds nothing that takes it, and is told none of that: the prompt arrives and reads
        // as unanswered, exactly as it does for the two seats that could act. Seat 2 learns nothing
        // from it, which is the point, and the window waits for its answer like anybody else's.
        var claim = table.ClaimFor(2);

        Assert.NotNull(claim);
        Assert.False(claim!.YouAnswered);
    }

    [Fact]
    public void The_discarder_gets_no_prompt()
    {
        Assert.Null(Window().ClaimFor(0));
    }

    [Fact]
    public void Nobody_gets_their_hand_arranged_for_them()
    {
        var view = GameViewBuilder.Build(Window().State, "ABC123", 1, Seats);

        Assert.NotNull(view.Seats[1].Concealed);
        Assert.Null(view.Seats[1].Groups);
    }

    [Fact]
    public void The_prompt_carries_no_deadline()
    {
        Assert.Null(Window().ClaimFor(1)!.DeadlineUtc);
    }

    [Fact]
    public void Only_the_seat_on_the_discarders_left_is_told_a_chow_is_open_at_all()
    {
        var table = Window();

        // The one thing an unassisted table does say about a claim, because it is not about anyone's
        // hand: everybody can see the tile and who threw it. It is what keeps a Chow button off the
        // three screens where pressing it could only ever be refused.
        Assert.True(table.ClaimFor(1)!.ChowPossible);
        Assert.False(table.ClaimFor(2)!.ChowPossible);
        Assert.False(table.ClaimFor(3)!.ChowPossible);
    }

    [Fact]
    public void A_seat_that_took_its_call_back_is_shown_the_calls_again()
    {
        var table = Window();

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        MahjongGame.Withdraw(table.State, 3);

        var claim = table.ClaimFor(3)!;

        // Nothing pressed and still owing the discard an answer: the dialog is back to the four
        // calls it opened with.
        Assert.Null(claim.PressedKind);
        Assert.Null(claim.DeadlineUtc);
        Assert.False(claim.YouAnswered);
        Assert.False(claim.YouClaimed);
    }

    // ---------------------------------------------------------------- a press in progress

    [Fact]
    public void A_seat_part_way_through_a_press_still_counts_as_unanswered()
    {
        var table = Window();
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        var claim = table.ClaimFor(3)!;

        // It has to stay unanswered or the dialog closes over the half of the answer it still owes.
        Assert.False(claim.YouAnswered);
        Assert.Equal(ClaimKind.Pung, claim.PressedKind);

        // And nothing is counting against it. The dialog used to show ten seconds here and take
        // the discard away at the end of them.
        Assert.Null(claim.DeadlineUtc);
    }

    [Fact]
    public void A_press_by_one_seat_is_not_shown_to_the_others()
    {
        var table = Window();
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        var claim = table.ClaimFor(1)!;

        Assert.Null(claim.PressedKind);
    }

    [Fact]
    public void A_chow_press_carries_no_deadline_either()
    {
        var table = Window();
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);

        var claim = table.ClaimFor(1)!;

        Assert.Equal(ClaimKind.Chow, claim.PressedKind);
        Assert.Null(claim.DeadlineUtc);
    }

    [Fact]
    public void Naming_the_tiles_marks_the_seat_answered()
    {
        var table = Window();

        // Seat 1 presses first and never finishes, which is what keeps the window open long enough
        // for there to still be a view to look at after seat 3 completes.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, table.PungTiles, Now.AddSeconds(4));

        var claim = table.ClaimFor(3)!;

        Assert.True(claim.YouAnswered);
        Assert.Null(claim.PressedKind);
    }

    [Fact]
    public void A_finished_call_is_what_puts_a_deadline_on_the_prompt()
    {
        var table = Window();

        // Seat 1 presses and never finishes, so there is still a window to look at.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        Assert.Null(table.ClaimFor(1)!.DeadlineUtc);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, table.PungTiles, Now.AddSeconds(20));

        // Now the whole table is on a clock, and it is the one thing a clock still means here:
        // how long there is to call over a call that has been made.
        Assert.Equal(Now.AddSeconds(20 + Manual.ClaimWindowSeconds), table.ClaimFor(1)!.DeadlineUtc);
    }

    [Fact]
    public void An_explicit_pass_marks_the_seat_answered_but_not_as_having_claimed()
    {
        var table = Window();
        MahjongGame.Pass(table.State, 3, Now);

        var claim = table.ClaimFor(3)!;

        // The client offers to draw through the window off this difference: a seat that passed has
        // nothing to lose by moving the game on, and a seat that called something does.
        Assert.True(claim.YouAnswered);
        Assert.False(claim.YouClaimed);
    }

    [Fact]
    public void A_half_made_press_already_counts_as_having_claimed()
    {
        var table = Window();
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        var claim = table.ClaimFor(3)!;

        Assert.False(claim.YouAnswered);
        Assert.True(claim.YouClaimed);
    }

    // ---------------------------------------------------------------- assist on is untouched

    [Fact]
    public void With_assist_on_the_prompt_still_spells_the_groups_out()
    {
        var table = Window(Assisted);
        var view = GameViewBuilder.Build(table.State, "ABC123", 3, Seats);

        Assert.True(view.Assisted);
        Assert.NotNull(view.Seats[3].Groups);
        Assert.Equal(ClaimKind.Pung, Assert.Single(view.Claim!.Candidates).Kind);

        // Nothing has been called on the tile, so nothing is timing it at either kind of table.
        Assert.Null(view.Claim.DeadlineUtc);

        // A seat holding nothing gets the prompt too - it is how the tile is put in front of it -
        // but with nothing spelled out on it, because there is nothing it could do with the tile.
        var nothing = table.ClaimFor(2);

        Assert.NotNull(nothing);
        Assert.Empty(nothing!.Candidates);
        Assert.False(nothing.YouAnswered);
    }
}
