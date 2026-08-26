using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

/// <summary>
/// A table with <see cref="RuleOptions.AssistEnabled"/> off plays the claim window differently, and
/// only the claim window. Nobody is told what the discard is good for, so a call is pressed first
/// and paid for afterwards, in two acts rather than one.
///
/// Neither act is on a clock, here or at an assisted table. Nobody is timed for spotting what a
/// tile is worth and nobody is timed for counting their own tiles against it: what ends a window is
/// the table answering it, a call taking the tile, or the next seat drawing.
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
    }

    [Fact]
    public void A_person_holding_nothing_still_answers_the_tile_for_themselves()
    {
        var table = ChowAndPung();
        Throw(table);

        // Seat 2 cannot take the tile. The server used to pass for it so the window would not wait,
        // and that is exactly the tell this setting is meant to remove: the seats the window waits
        // on are the seats holding something. So it waits on all three, and seat 2 says no itself.
        Assert.DoesNotContain(2, table.State.Pending!.Passed);

        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Pass(table.State, 3, Now);

        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        MahjongGame.Pass(table.State, 2, Now);

        Assert.Null(table.State.Pending);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
    }

    [Fact]
    public void A_discard_nobody_can_use_opens_a_window_all_the_same()
    {
        // Nothing here takes the tile, from anybody. Under the old rule no window opened at all,
        // so the three seats that were shown nothing knew the tile was dead - and the three that
        // were shown a window knew somebody could take it. Both halves of that are help.
        var table = TestTable.Build(t => t
            .Hand(0, "5c").Filler(0, 16, Contested)
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Filler(3, 16, Contested),
            rules: Manual);

        var events = MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        Assert.Contains(events, e => e is ClaimWindowOpened);
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);
        Assert.Empty(table.State.Pending!.Passed);
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
        MahjongGame.Pass(table.State, 2, Now);
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

        Assert.Null(table.State.Pending!.DeadlineUtc);
    }

    // ---------------------------------------------------------------- no clock on a half-made call

    [Fact]
    public void Pressing_pung_starts_no_clock_either()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        // A press used to buy ten seconds to name the tiles and then be dropped. It buys the tile
        // instead: the seat has called it, and the table waits for them to say what it costs.
        Assert.Null(table.State.Pending!.DeadlineUtc);
        Assert.True(table.State.Pending.Declared[3].AwaitingTiles);
    }

    [Fact]
    public void A_late_pung_still_beats_a_chow_that_was_named_long_before_it()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Pass(table.State, 2, Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddSeconds(9));
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(40));
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddSeconds(44));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
        Assert.Empty(table.State.Hands[1].Melds);
    }

    [Fact]
    public void Nothing_takes_a_half_made_call_off_the_seat_that_made_it()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Pass(table.State, 2, Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddSeconds(9));
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(40));

        // Half an hour later the pung is still seat 3's to finish. This is the whole of the change:
        // the chow underneath used to get the tile the moment seat 3 was ten seconds slow.
        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddMinutes(30)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddMinutes(31));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
        Assert.Empty(table.State.Hands[1].Melds);
    }

    [Fact]
    public void A_seat_can_take_as_long_as_it_likes_and_still_name_the_tiles()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Pass(table.State, 2, Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddHours(1)));

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddHours(1));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
    }

    [Fact]
    public void Pressing_the_same_call_twice_asks_for_the_tiles_and_costs_nothing()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        var again = Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(9)));

        Assert.Equal("AlreadyPressed", again.Code);

        // The refusal is a prompt, not a punishment: the call is exactly where it was.
        Assert.True(table.State.Pending!.Declared[3].AwaitingTiles);
        Assert.Null(table.State.Pending.DeadlineUtc);
    }

    [Fact]
    public void A_press_can_be_switched_to_another_kind_without_taking_it_back_first()
    {
        var table = ChowOrPung();

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Pung, [], Now);

        // With nothing counting down, a press is a guess that costs nothing to change. It used to
        // be spent the moment it was made, because it had started a clock the table waited on.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now.AddSeconds(1));

        Assert.Equal(ClaimKind.Chow, table.State.Pending!.Declared[1].Kind);
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
        MahjongGame.Pass(table.State, 2, Now);
        MahjongGame.Pass(table.State, 3, Now);
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
    public void The_next_seat_cannot_draw_through_a_call_somebody_is_still_making()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Throws<IllegalMoveException>(() => MahjongGame.Draw(table.State, 1, Now.AddSeconds(3)));

        // Nothing but seat 3 can lift that: no clock runs it out, so it waits until seat 3 either
        // names the tiles or lets the call go.
        Assert.Throws<IllegalMoveException>(() => MahjongGame.Draw(table.State, 1, Now.AddHours(1)));

        MahjongGame.Withdraw(table.State, 3);
        MahjongGame.Draw(table.State, 1, Now.AddHours(1));

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
    // immediately after the thrower - which here is a bot. Seat 1 stands in for the person who had
    // nothing either way, and the window waits on it exactly as hard as on seat 0.

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
    public void A_bots_discard_is_never_put_on_a_clock()
    {
        var table = BotThrows();

        // It used to get ten seconds, on the reasoning that nobody at the table would prompt each
        // other about a tile a machine threw. The people watching asked for the opposite: a bot
        // throws faster than anyone can look, so its discard waits for them instead.
        Assert.Null(table.State.Pending!.DeadlineUtc);

        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddMinutes(10)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);
    }

    [Fact]
    public void The_other_bot_is_answered_for_and_the_two_people_are_not()
    {
        var table = BotThrows();

        // Seat 3 is a bot holding nothing: there is no screen to show it the tile on, so the engine
        // says no for it rather than spending a tick of the game clock on it.
        Assert.Contains(3, table.State.Pending!.Passed);
        Assert.DoesNotContain(0, table.State.Pending.Passed);
        Assert.DoesNotContain(1, table.State.Pending.Passed);
    }

    [Fact]
    public void The_window_waits_for_every_person_not_only_the_one_holding_something()
    {
        var table = BotThrows();

        // Seat 0 is the only seat that could take the tile, and saying no is not enough on its own:
        // seat 1 is still looking at it.
        Assert.Empty(MahjongGame.Pass(table.State, 0, Now.AddSeconds(1)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        var events = MahjongGame.Pass(table.State, 1, Now.AddSeconds(30));

        Assert.NotEmpty(events);
        Assert.Null(table.State.Pending);
        Assert.Equal(3, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
    }

    [Fact]
    public void A_press_on_a_bots_discard_is_not_counting_either()
    {
        var table = BotThrows();

        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, [], Now.AddSeconds(8));

        Assert.Null(table.State.Pending!.DeadlineUtc);

        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, PungTilesOf(table, 0), Now.AddMinutes(6));

        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[0].Melds).Kind);
        Assert.Equal(0, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
    }

    [Fact]
    public void A_press_holds_the_window_open_however_long_it_takes()
    {
        var table = BotThrows();

        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Claim(table.State, 0, ClaimKind.Pung, [], Now.AddSeconds(8));

        // Every seat has answered by the count, but seat 0 still owes the tiles and time will not
        // take them off it. Only seat 0 can end this, by naming them or by letting the call go.
        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddHours(2)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);
        Assert.Empty(table.State.Hands[0].Melds);
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
        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Pass(table.State, 2, Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        Assert.Null(table.State.Pending);
        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
    }

    [Fact]
    public void With_assist_on_the_window_has_no_deadline_until_somebody_calls()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Assisted);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        // The six seconds used to start with the throw, which is what put a person who looked away
        // for a moment out of the discard for good.
        Assert.Null(table.State.Pending!.DeadlineUtc);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddMinutes(3));

        // Now something has been said out loud, and this is how long the rest of the table has to
        // say something better over it.
        Assert.Equal(Now.AddMinutes(3).AddSeconds(Assisted.ClaimWindowSeconds),
            table.State.Pending!.DeadlineUtc);
    }

    // ---------------------------------------------------------------- taking a call back

    /// <summary>
    /// Seat 1 holds 4-5-5-6 chars, so the same discard is both a chow and a pung for it. That is
    /// the hand a cancel is for: one press, two things it could have meant.
    /// </summary>
    private static TestTable ChowOrPung() => TestTable.Build(t => t
        .Hand(1, "4556c").Filler(1, 12, Contested)
        .Filler(2, 16, Contested)
        .Filler(3, 16, Contested)
        .Hand(0, "5c").Filler(0, 16, Contested),
        rules: Manual);

    [Fact]
    public void A_call_taken_back_frees_the_seat_to_press_a_different_one()
    {
        var table = ChowOrPung();
        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        MahjongGame.Claim(table.State, 1, ClaimKind.Pung, [], Now);
        MahjongGame.Withdraw(table.State, 1);

        // The press is gone, so the rule that a spent press cannot be switched has nothing to bite
        // on: this is a first press again.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now.AddSeconds(2));

        Assert.Equal(ClaimKind.Chow, table.State.Pending!.Declared[1].Kind);
    }

    [Fact]
    public void Taking_a_pung_back_puts_the_seat_where_it_was_before_it_pressed()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        MahjongGame.Withdraw(table.State, 3);

        // The seat is not treated as having passed: it is exactly where it was before it pressed.
        Assert.Null(table.State.Pending!.DeadlineUtc);
        Assert.DoesNotContain(3, table.State.Pending.Passed);
        Assert.False(table.State.Pending.Declared.ContainsKey(3));
    }

    [Fact]
    public void A_seat_that_took_its_call_back_still_owes_the_discard_an_answer()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        MahjongGame.Withdraw(table.State, 3);

        MahjongGame.Pass(table.State, 1, Now.AddSeconds(1));
        MahjongGame.Pass(table.State, 2, Now.AddSeconds(1));

        // Two of three have answered. Taking the call back was not the third answer.
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        MahjongGame.Pass(table.State, 3, Now.AddSeconds(2));

        Assert.Null(table.State.Pending);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
    }

    [Fact]
    public void A_taken_back_pung_lets_the_chow_underneath_it_through()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now.AddSeconds(1));
        MahjongGame.Withdraw(table.State, 3);
        MahjongGame.Pass(table.State, 2, Now.AddSeconds(2));

        // Without the cancel this tile sits under a pung nobody can pay for and nothing runs out.
        // With it, the chow ranked underneath has the tile the moment seat 3 says so.
        MahjongGame.Pass(table.State, 3, Now.AddSeconds(3));

        Assert.Equal(SetKind.Chow, Assert.Single(table.State.Hands[1].Melds).Kind);
    }

    [Fact]
    public void There_is_nothing_to_take_back_before_anything_is_pressed()
    {
        var table = ChowAndPung();
        Throw(table);

        Assert.Throws<IllegalMoveException>(() => MahjongGame.Withdraw(table.State, 3));
    }

    [Fact]
    public void A_claim_already_paid_for_can_still_be_let_go()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddSeconds(2));

        // Calling first holds the tile against the rest of the table, so letting go has to be a
        // thing a seat can do: otherwise a call made by mistake locks the tile up until it wins.
        MahjongGame.Withdraw(table.State, 1);

        Assert.Empty(table.State.Pending!.Declared);
        Assert.Null(table.State.Pending.DeadlineUtc);
        Assert.DoesNotContain(1, table.State.Pending.Passed);
    }

    [Fact]
    public void Letting_a_call_go_gives_back_the_seats_it_answered_for()
    {
        var table = ChowAndPung();
        Throw(table);

        // The chow presses first; the finished pung beats it and answers for it.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now.AddSeconds(1));

        Assert.Contains(1, table.State.Pending!.Outranked);

        // Seat 3 lets the pung go, so the tile is back on the table and seat 1 is waiting on it
        // again rather than having been answered for by a call that no longer exists.
        MahjongGame.Withdraw(table.State, 3);

        Assert.Empty(table.State.Pending!.Outranked);
        Assert.DoesNotContain(1, table.State.Pending.Passed);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, ChowTiles(table), Now.AddSeconds(2));
        MahjongGame.Pass(table.State, 2, Now.AddSeconds(3));
        MahjongGame.Pass(table.State, 3, Now.AddSeconds(4));

        Assert.Equal(SetKind.Chow, Assert.Single(table.State.Hands[1].Melds).Kind);
    }

    [Fact]
    public void A_call_left_standing_takes_the_tile_when_the_beat_runs_out()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now);

        // Seats 1 and 2 never answer. They heard the call and said nothing over it, which is the
        // one thing time is still allowed to read as an answer.
        var events = MahjongGame.ExpireClaimWindow(
            table.State, Now.AddSeconds(Manual.ClaimWindowSeconds));

        Assert.NotEmpty(events);
        Assert.Equal(SetKind.Pung, Assert.Single(table.State.Hands[3].Melds).Kind);
        Assert.Equal(3, table.State.CurrentSeat);
    }

    // ---------------------------------------------------------------- what the client may hide

    [Fact]
    public void A_chow_is_only_ever_open_to_the_seat_on_the_discarders_left()
    {
        var table = ChowAndPung();
        Throw(table);

        var tile = table.State.Pending!.Tile;

        Assert.True(MahjongGame.ChowPossible(table.State, tile, fromSeat: 0, seat: 1));
        Assert.False(MahjongGame.ChowPossible(table.State, tile, fromSeat: 0, seat: 2));
        Assert.False(MahjongGame.ChowPossible(table.State, tile, fromSeat: 0, seat: 3));
    }

    [Fact]
    public void No_seat_can_chow_a_wind_however_it_is_sitting()
    {
        var table = TestTable.Build(t => t
            .Hand(0, "1w").Filler(0, 16, "W1")
            .Filler(1, 16, "W1")
            .Filler(2, 16, "W1")
            .Filler(3, 16, "W1"),
            rules: Manual);

        MahjongGame.Discard(table.State, 0, table.HeldId(0, "W1"), Now);

        var tile = table.State.Pending!.Tile;

        Assert.False(MahjongGame.ChowPossible(table.State, tile, fromSeat: 0, seat: 1));
    }

    [Fact]
    public void With_the_left_only_rule_off_every_other_seat_could_chow()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Filler(2, 16, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Manual with { ChowFromLeftOnly = false });

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        var tile = table.State.Pending!.Tile;

        foreach (var seat in new[] { 1, 2, 3 })
            Assert.True(MahjongGame.ChowPossible(table.State, tile, fromSeat: 0, seat));
    }

    // ---------------------------------------------------------------- calls the table can hear
    //
    // A call at a real table is shouted, and the three seats that did not make it hear it at once.
    // Held silently until the window closed, it let a seat spend the whole window building a group
    // that the call had already beaten - which is what these cover.

    [Fact]
    public void A_finished_pung_answers_for_the_chow_it_beats()
    {
        var table = ChowAndPung();
        Throw(table);

        // The chow presses first and is still working out what it costs.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        Assert.True(table.State.Pending!.Declared[1].AwaitingTiles);

        // The pung lands complete, and nothing the chow could name would take the tile now.
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now);

        var pending = table.State.Pending!;

        Assert.DoesNotContain(1, pending.Declared.Keys);
        Assert.Contains(1, pending.Outranked);
        Assert.Contains(1, pending.Passed);
    }

    [Fact]
    public void A_beaten_seat_is_told_it_was_beaten_rather_than_read_as_having_passed()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now);

        // Both are true, and they are not the same thing: one is an answer the seat gave, the other
        // is one given for it. Only the second has a call to name as the reason.
        Assert.Contains(1, table.State.Pending!.Passed);
        Assert.Contains(1, table.State.Pending.Outranked);
    }

    [Fact]
    public void A_chow_cannot_be_pressed_once_a_pung_has_been_called()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now);

        var refused = Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now));

        Assert.Contains("Pung", refused.Message);
        Assert.Equal("Outranked", refused.Code);
    }

    [Fact]
    public void A_win_is_still_worth_calling_over_a_standing_pung()
    {
        var table = ChowAndPung();
        Throw(table);

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, PungTiles(table), Now);

        var live = MahjongGame.LiveKinds(table.State, table.State.Pending!, seat: 1);

        // Todas outranks a pung at this table, so it stays on offer. Nothing under it does, and no
        // second seat can hold the tiles for a pung or a kang off a face already punged.
        Assert.Equal([ClaimKind.Todas], live);
    }

    [Fact]
    public void A_chow_is_still_worth_calling_against_another_chow()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Hand(2, "46c").Filler(2, 14, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Manual with { ChowFromLeftOnly = false });

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 2, ClaimKind.Chow, [], Now);

        // Level with what is standing, so seating decides it rather than whoever pressed first -
        // and seat 1 sits nearer the discarder, so it would win. The press has to be allowed.
        Assert.Contains(ClaimKind.Chow, MahjongGame.LiveKinds(table.State, table.State.Pending!, seat: 1));
    }

    [Fact]
    public void The_next_seat_cannot_draw_through_a_chow_that_is_still_being_chosen()
    {
        var table = TestTable.Build(t => t
            .Hand(2, "46c").Filler(2, 14, Contested)
            .Filler(1, 16, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested),
            rules: Manual with { ChowFromLeftOnly = false });

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        // Seat 2 is mid-call and is not the seat due to play next, so its chow used to be binned
        // without a word: the guard watched the naming clocks, and a chow is never put on one.
        MahjongGame.Claim(table.State, 2, ClaimKind.Chow, [], Now);

        var refused = Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Draw(table.State, 1, Now));

        Assert.Contains("still choosing", refused.Message);
        Assert.Contains(2, table.State.Pending!.Declared.Keys);
    }
}
