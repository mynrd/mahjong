using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

/// <summary>
/// A table with <see cref="RuleOptions.AssistEnabled"/> off plays the claim window differently, and
/// only the claim window. Nobody is told what the discard is good for, so nobody can be held to a
/// six-second deadline for spotting it: the window has no deadline at all. What is on a clock is
/// the second half of a claim - having pressed Pung or Kang, saying which of your own tiles it
/// costs - because a pung that never completes is keeping a chow underneath it waiting.
/// </summary>
public class AssistOffTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static RuleOptions Manual => RuleOptions.Default with { JokerEnabled = false, AssistEnabled = false };

    private static RuleOptions Assisted => RuleOptions.Default with { JokerEnabled = false };

    /// <summary>Seat 0 throws this. Seat 1 sits on its left, so seat 1 is the one that may chow.</summary>
    private const string Contested = "C5";

    /// <summary>
    /// Seat 0 about to throw C5, seat 1 holding the chow and seat 3 holding the pung. This is the
    /// table the rule was written for: the chow presses first and takes its time, the pung notices
    /// late and outranks it anyway.
    /// </summary>
    private static TestTable ChowAndPung() => TestTable.Build(t => t
        .Hand(1, "46c").Filler(1, 14, Contested)      // can chow
        .Hand(3, "55c").Filler(3, 14, Contested)      // can pung
        .Filler(2, 16, Contested)                     // nothing
        .Hand(0, "5c").Filler(0, 16, Contested),
        rules: Manual);

    private static void Throw(TestTable table) =>
        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

    private static int[] ChowTiles(TestTable table) => [table.HeldId(1, "C4"), table.HeldId(1, "C6")];

    private static int[] PungTiles(TestTable table) =>
        table.State.Hands[3].Concealed.Where(t => t.Tile.Code == "C5").Select(t => t.Id).ToArray();

    // ---------------------------------------------------------------- the window itself

    [Fact]
    public void The_window_has_no_deadline_at_all()
    {
        var table = ChowAndPung();
        Throw(table);

        Assert.Null(table.State.Pending!.DeadlineUtc);
        Assert.Null(table.State.Pending.NextDeadline);
    }

    [Fact]
    public void A_seat_holding_nothing_is_passed_by_the_server_but_marked_as_not_having_answered()
    {
        var table = ChowAndPung();
        Throw(table);

        // Seat 2 cannot take the tile, so the window must not wait on it. It is still flagged as an
        // automatic pass, which is what keeps its buttons on screen: a strip that vanished by itself
        // would be the server telling seat 2 its hand was no good.
        Assert.Contains(2, table.State.Pending!.Passed);
        Assert.Contains(2, table.State.Pending.AutoPassed);
        Assert.DoesNotContain(1, table.State.Pending.AutoPassed);
    }

    [Fact]
    public void Nothing_resolves_while_a_seat_has_pressed_but_not_named_its_tiles()
    {
        var table = ChowAndPung();
        Throw(table);

        // Seat 1 presses Chow with nothing named, then seat 3 passes. Every seat has now answered by
        // the old count, but seat 1 still owes the tiles, so the tile cannot move.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Pass(table.State, 3, Now);

        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);
        Assert.NotNull(table.State.Pending);
    }

    [Fact]
    public void Naming_the_tiles_afterwards_is_what_completes_the_claim()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Pass(table.State, 3, Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddMinutes(4));

        Assert.Null(table.State.Pending);
        Assert.Equal(SetKind.Chow, Assert.Single(table.State.Hands[1].Melds).Kind);
    }

    [Fact]
    public void A_chow_press_is_not_put_on_a_clock()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);

        // Nothing is ranked under a chow, so nobody is being kept waiting by one.
        Assert.Empty(table.State.Pending!.NamingDeadline);
        Assert.Null(table.State.Pending.NextDeadline);
    }

    // ---------------------------------------------------------------- the naming clock

    [Fact]
    public void Pressing_pung_starts_the_naming_clock()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Equal(Now.AddSeconds(Manual.ClaimFulfilSeconds), table.State.Pending!.NamingDeadline[3]);
        Assert.True(table.State.Pending.Declared[3].AwaitingTiles);
    }

    [Fact]
    public void A_late_pung_still_beats_a_chow_that_was_named_long_before_it()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddSeconds(9));
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(40));
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddSeconds(44));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
        Assert.Empty(table.State.Hands[1].Melds);
    }

    [Fact]
    public void Running_out_of_time_to_name_the_tiles_hands_the_discard_to_the_chow_underneath()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddSeconds(9));
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(40));

        var events = MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(50));

        Assert.NotEmpty(events);
        Assert.Equal(SetKind.Chow, Assert.Single(table.State.Hands[1].Melds).Kind);
        Assert.Empty(table.State.Hands[3].Melds);
    }

    [Fact]
    public void The_clock_does_not_fire_before_it_is_due()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(9)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);
    }

    [Fact]
    public void A_seat_that_let_the_clock_run_out_cannot_press_again()
    {
        var table = ChowAndPung();
        Throw(table);

        // Seat 1 keeps the window open so there is still something to press on.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(10));

        Assert.Contains(3, table.State.Pending!.Burned);
        Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddSeconds(11)));
    }

    [Fact]
    public void Pressing_pung_twice_does_not_buy_another_ten_seconds()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        // Re-pressing is refused outright, so there is no path to a fresh clock.
        Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(9)));

        Assert.Equal(Now.AddSeconds(Manual.ClaimFulfilSeconds), table.State.Pending!.NamingDeadline[3]);
    }

    [Fact]
    public void A_spent_pung_press_cannot_be_switched_to_another_kind()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "455c").Filler(1, 13, Contested)     // can chow and pung
            .Filler(2, 16, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Manual);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Pung, [], Now);

        Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now.AddSeconds(1)));
    }

    [Fact]
    public void A_win_is_always_still_available_to_a_seat_that_pressed_pung_first()
    {
        // Seat 1 holds a pair of 5 chars inside 123d 456d 789d 111b 99b + 55c, so the same discard
        // is both a pung it could take and the tile that finishes its hand.
        var table = TestTable.Build(t => t
            .Hand(1, "123d 456d 789d 111b 99b 55c")
            .Filler(2, 16, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Manual);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Pung, [], Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Todas, [], Now.AddSeconds(3));

        Assert.Equal(GamePhase.HandOver, table.State.Phase);
        Assert.Equal(1, table.State.Outcome!.WinnerSeat);
    }

    // ---------------------------------------------------------------- the next seat drawing

    [Fact]
    public void The_next_seat_drawing_kills_a_discard_nobody_completed()
    {
        var table = ChowAndPung();
        Throw(table);

        // Seat 1 is the seat due to play next, and it wants a fresh tile instead of the chow.
        var events = MahjongGame.Draw(table.State, 1, Now.AddMinutes(2));

        Assert.Null(table.State.Pending);
        Assert.Empty(table.State.Hands[1].Melds);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.Equal(1, table.State.CurrentSeat);
        Assert.Contains(events, e => e is TileDrawn);
    }

    [Fact]
    public void Drawing_gives_up_the_drawers_own_half_made_claim()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Draw(table.State, 1, Now.AddMinutes(2));

        Assert.Empty(table.State.Hands[1].Melds);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
    }

    [Fact]
    public void A_claim_somebody_finished_beats_the_next_seat_drawing()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddSeconds(5));

        // Seat 3 has finished, but the window is still open on seat 1, who now gives up and draws.
        // The tile goes to the finished pung, and seat 1 does not get its turn after all.
        var events = MahjongGame.Draw(table.State, 1, Now.AddSeconds(30));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
        Assert.Equal(3, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.DoesNotContain(events, e => e is TileDrawn);
    }

    [Fact]
    public void The_next_seat_cannot_draw_through_a_naming_clock_that_is_still_running()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Throws<IllegalMoveException>(() => MahjongGame.Draw(table.State, 1, Now.AddSeconds(3)));

        // Once it lapses, the draw is allowed again.
        MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(10));
        MahjongGame.Draw(table.State, 1, Now.AddSeconds(11));

        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.Equal(1, table.State.CurrentSeat);
    }

    [Fact]
    public void A_seat_that_is_not_next_cannot_end_the_window_by_drawing()
    {
        var table = ChowAndPung();
        Throw(table);

        Assert.Throws<IllegalMoveException>(() => MahjongGame.Draw(table.State, 3, Now.AddMinutes(2)));
        Assert.NotNull(table.State.Pending);
    }

    // ---------------------------------------------------------------- a bot's discard
    //
    // The scenarios the rule was written from, with the same cast: seats 0 and 1 are the humans,
    // seats 2 and 3 are bots, and seat 2 is the one that throws. Seat 3 sits immediately after it,
    // so seat 3 is the seat that plays next once the tile dies.
    //
    // Only seat 0 can act on the tile. Four copies exist and the thrower is holding one, so two
    // seats cannot both be sitting on a pair of them, and a chow may only be taken by the seat
    // immediately after the thrower - which here is a bot. Seat 1 stands in for the human who had
    // nothing either way: the window never waits on it, and it is not told so.

    private static TestTable BotThrows()
    {
        var table = TestTable.Build(t => t
            .Hand(0, "55c").Filler(0, 14, Contested)      // human, can pung
            .Filler(1, 16, Contested)                     // human, nothing
            .Filler(3, 16, Contested)                     // bot, nothing
            .Hand(2, "5c").Filler(2, 16, Contested),      // bot, about to throw
            currentSeat: 2,
            rules: Manual);

        table.State.BotSeats.Add(2);
        table.State.BotSeats.Add(3);

        MahjongGame.Discard(table.State, 2, table.HeldId(2, Contested), Now);
        return table;
    }

    private static int[] PungTilesOf(TestTable table, int seat) =>
        table.State.Hands[seat].Concealed.Where(t => t.Tile.Code == "C5").Select(t => t.Id).ToArray();

    [Fact]
    public void A_bots_discard_is_the_one_that_gets_a_deadline()
    {
        var table = BotThrows();

        Assert.Equal(Now.AddSeconds(Manual.BotDiscardWindowSeconds), table.State.Pending!.DeadlineUtc);
    }

    [Fact]
    public void Saying_no_moves_the_game_on_without_waiting_out_the_ten_seconds()
    {
        var table = BotThrows();

        var events = MahjongGame.Pass(table.State, 0, Now.AddSeconds(1));

        Assert.NotEmpty(events);
        Assert.Null(table.State.Pending);
        Assert.Equal(3, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
    }

    [Fact]
    public void Nobody_answering_moves_the_game_on_at_ten_seconds()
    {
        var table = BotThrows();

        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(9)));

        var events = MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(10));

        Assert.NotEmpty(events);
        Assert.Null(table.State.Pending);
        Assert.Equal(3, table.State.CurrentSeat);
    }

    [Fact]
    public void A_press_made_in_time_keeps_its_full_ten_seconds_past_the_window()
    {
        var table = BotThrows();

        // Pressed at second 8, so the naming clock runs to second 18, well past the window.
        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, [], Now.AddSeconds(8));

        // The window is due, and nothing closes while somebody is still choosing.
        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(10)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, PungTilesOf(table, 0), Now.AddSeconds(14));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[0].Melds).Kind);
        Assert.Equal(0, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
    }

    [Fact]
    public void A_running_naming_clock_is_what_the_ticker_waits_for_not_the_passed_window()
    {
        var table = BotThrows();
        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, [], Now.AddSeconds(8));

        // The window deadline is already behind us at second 10, but nothing can happen until the
        // naming clock resolves, so that is what the window reports as due. Reporting the passed
        // deadline instead would wake the ticker every tick for ten seconds to do nothing.
        Assert.Equal(Now.AddSeconds(18), table.State.Pending!.NextDeadline);
    }

    [Fact]
    public void A_press_that_runs_out_lets_the_expired_window_close_on_the_same_tick()
    {
        var table = BotThrows();

        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, [], Now.AddSeconds(8));

        var events = MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(18));

        Assert.NotEmpty(events);
        Assert.Empty(table.State.Hands[0].Melds);
        Assert.Null(table.State.Pending);
        Assert.Equal(3, table.State.CurrentSeat);
    }

    [Fact]
    public void A_humans_discard_at_the_same_table_still_has_no_deadline()
    {
        var table = TestTable.Build(t => t
            .Hand(2, "46c").Filler(2, 14, Contested)
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Filler(1, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Manual);

        table.State.BotSeats.Add(2);
        table.State.BotSeats.Add(3);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        Assert.Null(table.State.Pending!.DeadlineUtc);
    }

    // ---------------------------------------------------------------- assist on is untouched

    [Fact]
    public void With_assist_on_a_claim_naming_no_tiles_still_resolves_on_the_spot()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Assisted);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Null(table.State.Pending);
        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
    }

    [Fact]
    public void With_assist_on_the_window_still_has_its_deadline()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Assisted);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        Assert.Equal(Now.AddSeconds(Assisted.ClaimWindowSeconds), table.State.Pending!.DeadlineUtc);
    }
}
