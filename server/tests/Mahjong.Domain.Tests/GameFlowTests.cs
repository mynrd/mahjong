using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

public class GameFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static RuleOptions NoJoker => RuleOptions.Default with { JokerEnabled = false };

    /// <summary>
    /// The standard set-up for the claim tests: seat 0 is about to discard the 5 chars, and the
    /// other seats are padded with tiles that cannot interact with it. Callers add the specific
    /// hands that make a claim possible before this runs.
    /// </summary>
    private const string Contested = "C5";

    // ---------------------------------------------------------------- dealing

    [Fact]
    public void Dealing_gives_every_seat_sixteen_tiles_and_the_mano_seventeen()
    {
        var (state, _) = MahjongGame.Deal(NoJoker, handNumber: 1, manoSeat: 0, seed: 12345, Now);

        Assert.Equal(17, state.Hands[0].TileCount);
        for (var seat = 1; seat < 4; seat++)
            Assert.Equal(16, state.Hands[seat].TileCount);
    }

    [Fact]
    public void The_mano_is_the_one_who_starts_with_the_extra_tile_whichever_seat_that_is()
    {
        var (state, _) = MahjongGame.Deal(NoJoker, handNumber: 3, manoSeat: 2, seed: 999, Now);

        Assert.Equal(2, state.CurrentSeat);
        Assert.Equal(17, state.Hands[2].TileCount);
        Assert.Equal(GamePhase.AwaitingDiscard, state.Phase);
    }

    [Fact]
    public void No_bonus_tile_is_ever_left_sitting_in_a_hand_after_the_deal()
    {
        // A replacement for a bonus tile can itself be a bonus tile, so this loop has to terminate
        // on every deal, not just most of them.
        for (var seed = 0; seed < 200; seed++)
        {
            var (state, _) = MahjongGame.Deal(NoJoker, 1, 0, seed, Now);

            foreach (var hand in state.Hands)
                Assert.DoesNotContain(hand.Concealed, t => t.Tile.IsBonus);
        }
    }

    [Fact]
    public void Every_one_of_the_144_tiles_is_accounted_for_after_the_deal()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var (state, _) = MahjongGame.Deal(NoJoker, 1, 0, seed, Now);

            var inHands = state.Hands.Sum(h => h.Concealed.Count + h.Bonus.Count);
            var undrawn = state.BackIndex - state.FrontIndex + 1;

            Assert.Equal(TileSet.TotalTiles, inHands + undrawn);
        }
    }

    [Fact]
    public void The_same_seed_deals_the_same_hand_so_a_game_can_be_replayed()
    {
        var (first, _) = MahjongGame.Deal(NoJoker, 1, 0, seed: 777, Now);
        var (second, _) = MahjongGame.Deal(NoJoker, 1, 0, seed: 777, Now);

        for (var seat = 0; seat < 4; seat++)
            Assert.Equal(
                first.Hands[seat].Concealed.Select(t => t.Id),
                second.Hands[seat].Concealed.Select(t => t.Id));
    }

    [Fact]
    public void Different_seeds_deal_different_hands()
    {
        var (first, _) = MahjongGame.Deal(NoJoker, 1, 0, seed: 1, Now);
        var (second, _) = MahjongGame.Deal(NoJoker, 1, 0, seed: 2, Now);

        Assert.NotEqual(
            first.Hands[0].Concealed.Select(t => t.Id),
            second.Hands[0].Concealed.Select(t => t.Id));
    }

    [Fact]
    public void A_seat_dealt_no_bonus_tiles_is_paid_the_no_flowers_ambition()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var (state, events) = MahjongGame.Deal(NoJoker, 1, 0, seed, Now);

            var barren = Enumerable.Range(0, 4).Where(s => state.Hands[s].Bonus.Count == 0).ToList();
            if (barren.Count == 0) continue;

            var paid = events.OfType<AmbitionEarned>()
                .Where(e => e.Ambition == Ambition.NoFlowers)
                .Select(e => e.Seat)
                .ToList();

            Assert.Equal(barren.Order(), paid.Order());
            return;
        }

        Assert.Fail("No seed in the sample produced a seat with zero bonus tiles.");
    }

    [Fact]
    public void The_joker_is_a_playable_face_and_is_announced()
    {
        var (state, events) = MahjongGame.Deal(RuleOptions.Default, 1, 0, seed: 42, Now);

        Assert.NotNull(state.Joker);
        Assert.True(state.Joker!.Value.IsPlayable);
        Assert.Equal(state.Joker.Value, events.OfType<JokerChosen>().Single().Joker);
    }

    [Fact]
    public void Turning_the_joker_rule_off_leaves_no_joker()
    {
        var (state, events) = MahjongGame.Deal(NoJoker, 1, 0, seed: 42, Now);

        Assert.Null(state.Joker);
        Assert.Empty(events.OfType<JokerChosen>());
    }

    // ---------------------------------------------------------------- turn order

    [Fact]
    public void A_seat_cannot_discard_when_it_is_not_its_turn()
    {
        var table = TestTable.Build(t => t
            .Hand(0, "123d 456d 789d 111b 222b 33c")
            .Filler(1, 16));

        var ex = Assert.Throws<IllegalMoveException>(
            () => MahjongGame.Discard(table.State, seat: 1, table.State.Hands[1].Concealed[0].Id, Now));

        Assert.Contains("seat 0's turn", ex.Message);
    }

    [Fact]
    public void A_seat_cannot_discard_a_tile_it_does_not_hold()
    {
        var table = TestTable.Build(t => t.Hand(0, "123d 456d 789d 111b 222b 33c"));

        // Tile 143 is the last season tile, which nobody was dealt here.
        Assert.Throws<IllegalMoveException>(() => MahjongGame.Discard(table.State, 0, tileId: 143, Now));
    }

    [Fact]
    public void A_discard_nobody_can_use_still_opens_a_window_for_the_table_to_answer()
    {
        var table = TestTable.Build(t => t
            .Hand(0, "9d").Filler(0, 16, "D9")
            .Filler(1, 16, "D9")
            .Filler(2, 16, "D9")
            .Filler(3, 16, "D9"));

        var events = MahjongGame.Discard(table.State, 0, table.HeldId(0, "D9"), Now);

        // Every discard opens one now, whether or not anything can be done with it: the window
        // opening is what puts the tile in front of the other three, and one that only opened when
        // somebody could claim would be announcing that somebody could.
        Assert.Contains(events, e => e is TileDiscarded);
        Assert.Contains(events, e => e is ClaimWindowOpened);
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        // It is people at all three seats here, so nothing has been answered for them.
        Assert.Empty(table.State.Pending!.Passed);

        // And nothing times them out of it either: the tile is still there half an hour later.
        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddMinutes(30)));
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        MahjongGame.Pass(table.State, 1, Now.AddMinutes(30));
        MahjongGame.Pass(table.State, 2, Now.AddMinutes(30));
        MahjongGame.Pass(table.State, 3, Now.AddMinutes(30));

        Assert.Equal(1, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
    }

    [Fact]
    public void A_discard_three_bots_can_do_nothing_with_moves_on_inside_the_same_move()
    {
        var table = TestTable.Build(t => t
            .Hand(0, "9d").Filler(0, 16, "D9")
            .Filler(1, 16, "D9")
            .Filler(2, 16, "D9")
            .Filler(3, 16, "D9"));

        for (var bot = 1; bot < 4; bot++) table.State.BotSeats.Add(bot);

        var events = MahjongGame.Discard(table.State, 0, table.HeldId(0, "D9"), Now);

        // Nobody is looking at this tile, so there is nothing to wait for and no reason to spend
        // three ticks of the game clock on three bots saying no.
        Assert.Contains(events, e => e is ClaimWindowClosed);
        Assert.Equal(1, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
    }

    [Fact]
    public void Drawing_puts_the_seat_into_discard_phase_holding_seventeen()
    {
        var table = TestTable.Build(
            t => t.Filler(1, 16),
            currentSeat: 1,
            phase: GamePhase.AwaitingDraw);

        Assert.Equal(16, table.State.Hands[1].TileCount);

        MahjongGame.Draw(table.State, 1, Now);

        Assert.Equal(17, table.State.Hands[1].TileCount);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.NotNull(table.State.JustDrew);
    }

    [Fact]
    public void A_bonus_tile_drawn_from_the_wall_is_exposed_and_replaced()
    {
        var table = TestTable.Build(
            t => t.Filler(1, 16).NextDraw("1f"),
            currentSeat: 1,
            phase: GamePhase.AwaitingDraw);

        var events = MahjongGame.Draw(table.State, 1, Now);

        Assert.Contains(events, e => e is BonusExposed);
        Assert.Single(table.State.Hands[1].Bonus);
        Assert.Equal(17, table.State.Hands[1].TileCount);
        Assert.DoesNotContain(table.State.Hands[1].Concealed, t => t.Tile.IsBonus);
    }

    // ---------------------------------------------------------------- claims

    [Fact]
    public void A_chow_can_only_be_claimed_by_the_seat_immediately_after_the_discarder()
    {
        // Seat 1 sits immediately after seat 0. Seat 2 holds the same run partners but is not on
        // seat 0's left, so the same tiles do not give it a chow.
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Hand(2, "46c").Filler(2, 14, Contested)
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        var allowed = MahjongGame.AllowedClaims(table.State, new TileRef(table.HeldId(0, Contested)), fromSeat: 0);

        Assert.Contains(ClaimKind.Chow, allowed[1]);
        Assert.False(allowed.TryGetValue(2, out var seat2) && seat2.Contains(ClaimKind.Chow));
        Assert.Contains(ClaimKind.Pung, allowed[3]);
    }

    [Fact]
    public void Turning_off_the_left_only_rule_lets_any_seat_chow()
    {
        var table = TestTable.Build(
            t => t
                .Hand(2, "46c").Filler(2, 14, Contested)
                .Hand(0, "5c").Filler(0, 16, Contested),
            rules: NoJoker with { ChowFromLeftOnly = false });

        var allowed = MahjongGame.AllowedClaims(table.State, new TileRef(table.HeldId(0, Contested)), fromSeat: 0);

        Assert.Contains(ClaimKind.Chow, allowed[2]);
    }

    [Fact]
    public void A_pung_claim_beats_a_chow_claim_on_the_same_discard()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)      // can chow
            .Hand(3, "55c").Filler(3, 14, Contested)      // can pung
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [table.HeldId(1, "C4"), table.HeldId(1, "C6")], Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        var events = MahjongGame.Pass(table.State, 2, Now);

        var meld = events.OfType<MeldFormed>().Single();
        Assert.Equal(3, meld.Seat);
        Assert.Equal(SetKind.Pung, meld.Meld.Kind);
        Assert.Equal(3, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
    }

    [Fact]
    public void A_declared_win_beats_a_pung_on_the_same_discard()
    {
        var table = TestTable.Build(t => t
            // Seat 2 is one 5 chars away from 123d 456d 789d 111b 222b + 55c.
            .Hand(2, "123d 456d 789d 111b 222b 5c")
            .Hand(1, "55c").Filler(1, 14, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        MahjongGame.Claim(table.State, 1, ClaimKind.Pung, [], Now);
        MahjongGame.Claim(table.State, 2, ClaimKind.Todas, [], Now);
        var events = MahjongGame.Pass(table.State, 3, Now);

        var ended = events.OfType<HandEnded>().Single();
        Assert.Equal(HandEndReason.Todas, ended.Outcome.Reason);
        Assert.Equal(2, ended.Outcome.WinnerSeat);
        Assert.Equal(GamePhase.HandOver, table.State.Phase);
    }

    /// <summary>
    /// Seat 3 has four groups down and 5c 5c 9b 9b in hand. Punging the thrown 5 chars makes the
    /// fifth group and leaves the 9 sticks as the pair - the hand is complete standing there, with
    /// nothing drawn. Discard used to be the only move it had.
    /// </summary>
    private static TestTable PungThatFinishesTheHand(string tail = "99b")
    {
        var table = TestTable.Build(t => t
            .Meld(3, SetKind.Chow, "123d")
            .Meld(3, SetKind.Chow, "456d")
            .Meld(3, SetKind.Chow, "789d")
            .Meld(3, SetKind.Chow, "234b")
            .Hand(3, $"55c {tail}")
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);
        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Pass(table.State, 2, Now);

        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.Equal(3, table.State.CurrentSeat);
        Assert.Null(table.State.JustDrew);

        return table;
    }

    [Fact]
    public void A_pung_that_completes_the_hand_can_still_be_declared_as_todas()
    {
        var table = PungThatFinishesTheHand();

        var ended = MahjongGame.DeclareTodasOnDraw(table.State, 3).OfType<HandEnded>().Single();

        Assert.Equal(HandEndReason.Todas, ended.Outcome.Reason);
        Assert.Equal(3, ended.Outcome.WinnerSeat);
        Assert.Equal(GamePhase.HandOver, table.State.Phase);
    }

    [Fact]
    public void A_win_declared_after_a_pung_is_paid_by_the_thrower_and_is_not_bunot()
    {
        // The winning tile came off seat 0's throw. Declaring it on your own turn rather than in
        // the claim window must not turn it into a self-drawn win: bunot doubles the total and
        // spreads the cost over all three seats instead of charging the one who fed it.
        var table = PungThatFinishesTheHand();

        var ended = MahjongGame.DeclareTodasOnDraw(table.State, 3).OfType<HandEnded>().Single();
        var score = ended.Outcome.Score!;
        var settlements = ended.Outcome.Settlements.ToDictionary(s => s.Seat);

        Assert.Equal(score.BaseUnits + score.BonusUnits, score.TotalUnits);

        var fed = score.TotalUnits * RuleOptions.Default.Scoring.DiscarderMultiplier;

        Assert.Equal(-fed, settlements[0].Delta);
        Assert.Equal("Fed the winning tile", settlements[0].Reason);
        Assert.Equal(-score.TotalUnits, settlements[1].Delta);
        Assert.Equal(-score.TotalUnits, settlements[2].Delta);
        Assert.Equal(fed + score.TotalUnits * 2, settlements[3].Delta);
    }

    [Fact]
    public void A_pung_that_leaves_two_odd_tiles_is_still_only_a_discard()
    {
        // The guard the change hangs on: 9b 8b left in hand is not a pair, so there is nothing to
        // declare and the seat has to throw one.
        var table = PungThatFinishesTheHand(tail: "89b");

        var error = Assert.Throws<IllegalMoveException>(() => MahjongGame.DeclareTodasOnDraw(table.State, 3));

        Assert.Contains("not complete", error.Message);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
    }

    [Fact]
    public void When_two_seats_declare_the_same_kind_the_one_nearer_after_the_discarder_wins()
    {
        // Two seats cannot both pung the same tile (that would need five copies), so this uses two
        // competing chows with the left-only rule off.
        var table = TestTable.Build(
            t => t
                .Hand(2, "46c").Filler(2, 14, Contested)
                .Hand(3, "46c").Filler(3, 14, Contested)
                .Filler(1, 16, Contested)
                .Hand(0, "5c").Filler(0, 16, Contested),
            rules: NoJoker with { ChowFromLeftOnly = false });

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        MahjongGame.Claim(table.State, 3, ClaimKind.Chow, [table.HeldId(3, "C4"), table.HeldId(3, "C6")], Now);
        MahjongGame.Claim(table.State, 2, ClaimKind.Chow, [table.HeldId(2, "C4"), table.HeldId(2, "C6")], Now);
        var events = MahjongGame.Pass(table.State, 1, Now);

        Assert.Equal(2, events.OfType<MeldFormed>().Single().Seat);
    }

    [Fact]
    public void A_seat_cannot_claim_its_own_discard()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        Assert.Throws<IllegalMoveException>(() => MahjongGame.Claim(table.State, 0, ClaimKind.Pung, [], Now));
    }

    [Fact]
    public void When_everyone_passes_the_tile_stays_discarded_and_play_moves_on()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Pass(table.State, 2, Now);
        var events = MahjongGame.Pass(table.State, 3, Now);

        Assert.Contains(events, e => e is ClaimWindowClosed);
        Assert.Equal(1, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDraw, table.State.Phase);
        Assert.False(table.State.Discards[^1].Claimed);
    }

    [Fact]
    public void The_next_seat_drawing_is_what_reads_the_rest_of_the_table_as_passing()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "46c").Filler(1, 14, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        Assert.Equal(GamePhase.AwaitingClaims, table.State.Phase);

        // Time used to do this after six seconds. It does not any more: seat 1 could have chowed
        // that tile, and it is still there for it until seat 1 itself decides to pick up instead.
        Assert.Empty(MahjongGame.ExpireClaimWindow(table.State, Now.AddSeconds(30)));

        MahjongGame.Draw(table.State, 1, Now.AddSeconds(30));

        Assert.Equal(1, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.False(table.State.Discards[^1].Claimed);
    }

    [Fact]
    public void A_claim_skips_the_seats_sitting_between_the_discarder_and_the_claimant()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "55c").Filler(3, 14, Contested)
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, [], Now);

        // Nothing moves until every seat has answered, whatever it is holding, because a pung is
        // not the highest thing that could still be called on the tile.
        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Pass(table.State, 2, Now);

        // Seats 1 and 2 never get a turn.
        Assert.Equal(3, table.State.CurrentSeat);
        Assert.Equal(GamePhase.AwaitingDiscard, table.State.Phase);
        Assert.Equal(17, table.State.Hands[3].TileCount);
    }

    // ---------------------------------------------------------------- kang family

    [Fact]
    public void Claiming_a_kang_pays_the_ambition_and_takes_a_replacement_tile()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "555c").Filler(3, 13, Contested)
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5c").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Kang, [], Now);
        MahjongGame.Pass(table.State, 1, Now);
        var events = MahjongGame.Pass(table.State, 2, Now);

        var ambition = events.OfType<AmbitionEarned>().Single();
        Assert.Equal(Ambition.Kang, ambition.Ambition);
        Assert.Equal(3, ambition.Seat);
        Assert.Equal(3, ambition.Settlements.Single(s => s.Seat == 3).Delta);

        Assert.Contains(events, e => e is TileDrawn { Replacement: true });
        Assert.Equal(17, table.State.Hands[3].TileCount);
    }

    [Fact]
    public void A_secret_kang_stays_concealed_and_pays_more_than_an_open_one()
    {
        var table = TestTable.Build(t => t.Hand(0, "5555c").Filler(0, 13, Contested));

        var events = MahjongGame.DeclareSecretKang(table.State, 0, Tile.Parse(Contested));

        var meld = events.OfType<MeldFormed>().Single().Meld;
        Assert.Equal(SetKind.Kang, meld.Kind);
        Assert.True(meld.Concealed);

        var ambition = events.OfType<AmbitionEarned>().Single();
        Assert.Equal(Ambition.SecretKang, ambition.Ambition);
        Assert.Equal(6, ambition.Settlements.Single(s => s.Seat == 0).Delta);
        Assert.Equal(17, table.State.Hands[0].TileCount);
    }

    [Fact]
    public void A_secret_kang_needs_all_four_copies()
    {
        var table = TestTable.Build(t => t.Hand(0, "555c").Filler(0, 14, Contested));

        Assert.Throws<IllegalMoveException>(
            () => MahjongGame.DeclareSecretKang(table.State, 0, Tile.Parse(Contested)));
    }

    [Fact]
    public void Sagasa_turns_an_exposed_pung_into_a_kang_and_pays()
    {
        var table = TestTable.Build(t => t
            .Meld(0, SetKind.Pung, "555c")
            .Hand(0, "5c").Filler(0, 13, Contested));

        var events = MahjongGame.DeclareSagasa(table.State, 0, Tile.Parse(Contested));

        var meld = events.OfType<MeldFormed>().Single().Meld;
        Assert.Equal(SetKind.Kang, meld.Kind);
        Assert.True(meld.FromSagasa);
        Assert.Equal(4, meld.Tiles.Count);

        Assert.Equal(Ambition.Sagasa, events.OfType<AmbitionEarned>().Single().Ambition);
        Assert.Equal(17, table.State.Hands[0].TileCount);
    }

    [Fact]
    public void Sagasa_needs_an_exposed_pung_of_that_face_to_extend()
    {
        var table = TestTable.Build(t => t.Hand(0, "5c").Filler(0, 16, Contested));

        Assert.Throws<IllegalMoveException>(
            () => MahjongGame.DeclareSagasa(table.State, 0, Tile.Parse(Contested)));
    }

    // ---------------------------------------------------------------- ending the hand

    [Fact]
    public void Declaring_todas_on_a_self_drawn_tile_is_a_bunot_and_ends_the_hand()
    {
        var table = TestTable.Build(t => t.Hand(0, "123d 456d 789d 111b 222b 33c"));
        table.State.JustDrew = table.State.Hands[0].Concealed[^1];

        var events = MahjongGame.DeclareTodasOnDraw(table.State, 0);
        var outcome = events.OfType<HandEnded>().Single().Outcome;

        Assert.Equal(HandEndReason.Todas, outcome.Reason);
        Assert.Equal(0, outcome.WinnerSeat);
        Assert.Equal(0, outcome.Settlements.Sum(s => s.Delta));

        // Self-drawn, so all three losers pay the same doubled amount.
        var losses = outcome.Settlements.Where(s => s.Seat != 0).Select(s => s.Delta).Distinct();
        Assert.Single(losses);
    }

    [Fact]
    public void Declaring_todas_on_an_incomplete_hand_is_refused()
    {
        var table = TestTable.Build(t => t.Hand(0, "123d 456d 789d 111b 222b 39c"));
        table.State.JustDrew = table.State.Hands[0].Concealed[^1];

        Assert.Throws<IllegalMoveException>(() => MahjongGame.DeclareTodasOnDraw(table.State, 0));
    }

    [Fact]
    public void When_the_wall_runs_out_the_hand_ends_with_nobody_paying()
    {
        var table = TestTable.Build(
            t => t.Filler(1, 16),
            currentSeat: 1,
            phase: GamePhase.AwaitingDraw);

        table.State.FrontIndex = table.State.BackIndex + 1;

        var events = MahjongGame.Draw(table.State, 1, Now);
        var outcome = events.OfType<HandEnded>().Single().Outcome;

        Assert.Equal(HandEndReason.WallExhausted, outcome.Reason);
        Assert.Null(outcome.WinnerSeat);
        Assert.Empty(outcome.Settlements);
        Assert.Equal(GamePhase.HandOver, table.State.Phase);
    }

    // ---------------------------------------------------------------- whole-hand invariants

    [Fact]
    public void Tiles_are_never_created_or_destroyed_over_a_long_random_game()
    {
        var rng = new Random(4242);
        var (state, _) = MahjongGame.Deal(NoJoker, 1, 0, seed: 4242, Now);

        var guard = 0;
        while (state.Phase != GamePhase.HandOver && guard++ < 2000)
        {
            Step(state, rng);

            var accounted =
                state.Hands.Sum(h => h.Concealed.Count + h.Melds.Sum(m => m.Tiles.Count) + h.Bonus.Count)
                + state.Discards.Count(d => !d.Claimed)
                + Math.Max(0, state.BackIndex - state.FrontIndex + 1);

            Assert.Equal(TileSet.TotalTiles, accounted);
        }

        Assert.Equal(GamePhase.HandOver, state.Phase);
    }

    [Fact]
    public void A_seat_holds_sixteen_tiles_between_turns_and_seventeen_during_one()
    {
        var rng = new Random(31337);
        var (state, _) = MahjongGame.Deal(NoJoker, 1, 0, seed: 31337, Now);

        var guard = 0;
        while (state.Phase != GamePhase.HandOver && guard++ < 2000)
        {
            if (state.Phase == GamePhase.AwaitingDraw)
                foreach (var hand in state.Hands)
                    Assert.Equal(16, hand.TileCount);

            if (state.Phase == GamePhase.AwaitingDiscard)
                Assert.Equal(17, state.Hands[state.CurrentSeat].TileCount);

            Step(state, rng);
        }
    }

    [Fact]
    public void Many_random_hands_all_reach_a_clean_end()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var (state, _) = MahjongGame.Deal(NoJoker, 1, seed % 4, seed, Now);

            var guard = 0;
            while (state.Phase != GamePhase.HandOver && guard++ < 3000) Step(state, rng);

            Assert.Equal(GamePhase.HandOver, state.Phase);
            Assert.NotNull(state.Outcome);
            Assert.Equal(0, state.Outcome!.Settlements.Sum(s => s.Delta));
        }
    }

    /// <summary>Plays one step of a hand at random, whatever phase it is in.</summary>
    private static void Step(GameState state, Random rng)
    {
        switch (state.Phase)
        {
            case GamePhase.AwaitingDraw:
                MahjongGame.Draw(state, state.CurrentSeat, Now);
                break;

            case GamePhase.AwaitingDiscard:
                var hand = state.Hands[state.CurrentSeat].Concealed;
                MahjongGame.Discard(state, state.CurrentSeat, hand[rng.Next(hand.Count)].Id, Now);
                break;

            case GamePhase.AwaitingClaims:
                // Nothing times a window out any more, so the way past one is the way it is at a
                // real table: the seat due to play next picks up and the discard is dead.
                MahjongGame.Draw(state, GameState.NextSeat(state.CurrentSeat), Now);
                break;
        }
    }
}
