using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

/// <summary>
/// <see cref="MahjongGame.ClaimCandidates"/> is the one place that answers "which groups could this
/// seat make with that tile". The claim window offers it, the client highlights off it, and
/// <see cref="MahjongGame.Claim"/> validates against it - so if it is wrong, all three are wrong
/// the same way.
/// </summary>
public class ClaimCandidateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static RuleOptions NoJoker => RuleOptions.Default with { JokerEnabled = false };

    /// <summary>Seat 0 throws this. Seat 1 sits on its left, so seat 1 is the one that may chow.</summary>
    private const string Contested = "B5";

    private static IReadOnlyList<ClaimCandidate> CandidatesFor(TestTable table, int seat) =>
        MahjongGame.ClaimCandidates(
            table.State, new TileRef(table.HeldId(0, Contested)), fromSeat: 0, seat);

    private static string[] Faces(ClaimCandidate candidate) =>
        candidate.Support.Select(t => t.Tile.Code).ToArray();

    /// <summary>
    /// Says no for every seat that is neither the thrower nor the one claiming, and hands back the
    /// events of the last of them. A window waits for all three seats however plainly the tile is
    /// already spoken for, so this is what makes a claim actually resolve.
    /// </summary>
    private static List<GameEvent> PassRest(TestTable table, int claimant)
    {
        var events = new List<GameEvent>();

        for (var seat = 1; seat < 4; seat++)
            if (seat != claimant)
                events = MahjongGame.Pass(table.State, seat, Now);

        return events;
    }

    // ---------------------------------------------------------------- what comes back

    [Fact]
    public void Two_copies_in_hand_give_one_pung_candidate_naming_both()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "55b").Filler(3, 14, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var candidate = Assert.Single(CandidatesFor(table, 3));

        Assert.Equal(ClaimKind.Pung, candidate.Kind);
        Assert.Equal(table.State.Hands[3].Concealed.Where(t => t.Tile.Code == "B5").Select(t => t.Id).Order(),
            candidate.Support.Select(t => t.Id).Order());
    }

    [Fact]
    public void Three_copies_give_a_kang_and_a_pung_in_that_order()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "555b").Filler(3, 13, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var candidates = CandidatesFor(table, 3);

        Assert.Equal([ClaimKind.Kang, ClaimKind.Pung], candidates.Select(c => c.Kind));
        Assert.Equal(3, candidates[0].Support.Count);
        Assert.Equal(2, candidates[1].Support.Count);
    }

    [Fact]
    public void One_possible_run_gives_one_chow_candidate()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "34b").Filler(1, 14, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var candidate = Assert.Single(CandidatesFor(table, 1));

        Assert.Equal(ClaimKind.Chow, candidate.Kind);
        Assert.Equal(["B3", "B4"], Faces(candidate));
    }

    [Fact]
    public void Every_distinct_run_is_offered_separately()
    {
        // B3 B4 B6 B7 with a B5 thrown reads three ways: 3-4-5, 4-5-6 and 5-6-7.
        var table = TestTable.Build(t => t
            .Hand(1, "3467b").Filler(1, 12, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var candidates = CandidatesFor(table, 1);

        Assert.All(candidates, c => Assert.Equal(ClaimKind.Chow, c.Kind));
        Assert.Equal(
            [["B3", "B4"], ["B4", "B6"], ["B6", "B7"]],
            candidates.Select(Faces));
    }

    [Fact]
    public void A_tile_claimable_two_ways_appears_in_both_candidates()
    {
        // Seat 1 holds B4 B4 B2 B3 and B4 is thrown: pung of 4s, or the 2-3-4 run.
        var table = TestTable.Build(t => t
            .Hand(1, "4423b").Filler(1, 12, "B4")
            .Hand(0, "4b").Filler(0, 16, "B4"));

        var candidates = MahjongGame.ClaimCandidates(
            table.State, new TileRef(table.HeldId(0, "B4")), fromSeat: 0, seat: 1);

        Assert.Equal([ClaimKind.Pung, ClaimKind.Chow], candidates.Select(c => c.Kind));

        var pung = candidates[0];
        var chow = candidates[1];

        Assert.Equal(["B4", "B4"], Faces(pung));
        Assert.Equal(["B2", "B3"], Faces(chow));

        // The B2 and B3 are only ever part of the chow; the B4s show up in the pung only. A tile
        // in more than one candidate is what the client colours differently, and here that is the
        // discarded face itself rather than anything in hand.
        var b3 = table.HeldId(1, "B3");
        Assert.DoesNotContain(b3, pung.Support.Select(t => t.Id));
        Assert.Contains(b3, chow.Support.Select(t => t.Id));
    }

    [Fact]
    public void A_pung_that_also_finishes_the_hand_offers_todas_first()
    {
        // Seat 3 has four groups already down and B5 B5 C9 C9 left in hand. The B5 thrown makes
        // the fifth group, and the C9s are the pair - so the same press that would pung it wins
        // the hand outright. Todas has to be on the window, and has to be the first thing on it.
        var table = TestTable.Build(t => t
            .Meld(3, SetKind.Chow, "123d")
            .Meld(3, SetKind.Chow, "456d")
            .Meld(3, SetKind.Chow, "789d")
            .Meld(3, SetKind.Chow, "234c")
            .Hand(3, "55b 99c")
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var candidates = CandidatesFor(table, 3);

        Assert.Equal([ClaimKind.Todas, ClaimKind.Pung], candidates.Select(c => c.Kind));
        Assert.Equal(["Todas", "Pung B5"], candidates.Select(c => c.Describe(new TileRef(table.HeldId(0, Contested)))));

        // And pressing it ends the hand there, rather than melding a pung and playing on.
        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Todas, [], Now);
        var events = PassRest(table, claimant: 3);

        var ended = events.OfType<HandEnded>().Single();

        Assert.Equal(HandEndReason.Todas, ended.Outcome.Reason);
        Assert.Equal(3, ended.Outcome.WinnerSeat);
        Assert.Equal(GamePhase.HandOver, table.State.Phase);
    }

    [Fact]
    public void A_seat_that_is_not_on_the_discarders_left_gets_no_chow_candidate()
    {
        var table = TestTable.Build(t => t
            .Hand(2, "443b").Filler(2, 13, "B4")
            .Hand(0, "4b").Filler(0, 16, "B4"));

        var candidates = MahjongGame.ClaimCandidates(
            table.State, new TileRef(table.HeldId(0, "B4")), fromSeat: 0, seat: 2);

        Assert.Equal([ClaimKind.Pung], candidates.Select(c => c.Kind));
    }

    [Fact]
    public void A_joker_is_not_wild_when_a_discard_is_claimed()
    {
        // Seat 3 holds two jokers and nothing else that matches. A joker completes a hand, but it
        // cannot stand in for a tile to make a pung off somebody else's throw.
        var table = TestTable.Build(
            t => t
                .Hand(3, "99c").Filler(3, 14, Contested, "C9")
                .Hand(0, "5b").Filler(0, 16, Contested, "C9"),
            joker: Tile.Parse("C9"));

        Assert.Empty(CandidatesFor(table, 3));
    }

    [Fact]
    public void The_discarding_seat_has_no_candidates_of_its_own()
    {
        var table = TestTable.Build(t => t
            .Hand(0, "555b").Filler(0, 14, Contested));

        Assert.Empty(CandidatesFor(table, 0));
    }

    [Fact]
    public void Allowed_claims_lists_exactly_the_kinds_the_candidates_carry()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "3467b").Filler(1, 12, Contested)
            .Hand(3, "555b").Filler(3, 13, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var discard = new TileRef(table.HeldId(0, Contested));
        var allowed = MahjongGame.AllowedClaims(table.State, discard, fromSeat: 0);

        Assert.Equal([ClaimKind.Chow], allowed[1]);
        Assert.Equal([ClaimKind.Kang, ClaimKind.Pung], allowed[3]);
        Assert.False(allowed.ContainsKey(2));
    }

    // ---------------------------------------------------------------- claiming with named tiles

    [Fact]
    public void Naming_the_second_run_forms_that_run_and_not_the_lowest_one()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "3467b").Filler(1, 12, Contested)
            .Filler(2, 16, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        // 5-6-7, the highest of the three runs on offer.
        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [table.HeldId(1, "B6"), table.HeldId(1, "B7")], Now);
        var events = PassRest(table, claimant: 1);

        var meld = events.OfType<MeldFormed>().Single();

        Assert.Equal(SetKind.Chow, meld.Meld.Kind);
        Assert.Equal(["B5", "B6", "B7"], meld.Meld.Tiles.Select(t => t.Tile.Code).Order());
        Assert.DoesNotContain(table.State.Hands[1].Concealed, t => t.Tile.Code is "B6" or "B7");

        // The tiles the old auto-pick would have taken are still in hand.
        Assert.Contains(table.State.Hands[1].Concealed, t => t.Tile.Code == "B3");
        Assert.Contains(table.State.Hands[1].Concealed, t => t.Tile.Code == "B4");
    }

    [Fact]
    public void Naming_tiles_that_match_no_candidate_is_refused()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "3467b").Filler(1, 12, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        // B3 and B6 are both held, but 3-5-6 is not a run.
        Assert.Throws<IllegalMoveException>(() => MahjongGame.Claim(
            table.State, 1, ClaimKind.Chow, [table.HeldId(1, "B3"), table.HeldId(1, "B6")], Now));
    }

    [Fact]
    public void A_pung_named_with_tiles_that_are_not_the_discarded_face_is_refused()
    {
        // The old code ignored the tile ids on a pung entirely, so this went through and the
        // server quietly melded a different pair of tiles than the ones the player picked.
        var table = TestTable.Build(t => t
            .Hand(3, "55b34b").Filler(3, 12, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        Assert.Throws<IllegalMoveException>(() => MahjongGame.Claim(
            table.State, 3, ClaimKind.Pung, [table.HeldId(3, "B3"), table.HeldId(3, "B4")], Now));
    }

    [Fact]
    public void Claiming_with_no_tiles_named_leaves_the_server_to_pick()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "3467b").Filler(1, 12, Contested)
            .Filler(2, 16, Contested)
            .Filler(3, 16, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        MahjongGame.Claim(table.State, 1, ClaimKind.Chow, [], Now);
        var events = PassRest(table, claimant: 1);
        var meld = events.OfType<MeldFormed>().Single();

        // Unchanged behaviour: the lowest run.
        Assert.Equal(["B3", "B4", "B5"], meld.Meld.Tiles.Select(t => t.Tile.Code).Order());
    }

    [Fact]
    public void A_named_pung_takes_the_copies_the_player_picked()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "555b").Filler(3, 13, Contested)
            .Filler(1, 16, Contested)
            .Filler(2, 16, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Contested), Now);

        // The two highest-numbered copies, which is not what the first-N fallback would take.
        var picked = table.State.Hands[3].Concealed
            .Where(t => t.Tile.Code == "B5")
            .OrderByDescending(t => t.Id)
            .Take(2)
            .Select(t => t.Id)
            .ToList();

        MahjongGame.Claim(table.State, 3, ClaimKind.Pung, picked, Now);
        var events = PassRest(table, claimant: 3);
        var meld = events.OfType<MeldFormed>().Single();

        Assert.Equal(SetKind.Pung, meld.Meld.Kind);
        Assert.All(picked, id => Assert.Contains(id, meld.Meld.Tiles.Select(t => t.Id)));
        Assert.Single(table.State.Hands[3].Concealed, t => t.Tile.Code == "B5");
    }

    // ---------------------------------------------------------------- button labels

    [Fact]
    public void A_candidate_describes_itself_as_the_group_it_would_form()
    {
        var table = TestTable.Build(t => t
            .Hand(1, "3467b").Filler(1, 12, Contested)
            .Hand(3, "555b").Filler(3, 13, Contested)
            .Hand(0, "5b").Filler(0, 16, Contested));

        var discard = new TileRef(table.HeldId(0, Contested));

        Assert.Equal(
            ["Chow B3-B4-B5", "Chow B4-B5-B6", "Chow B5-B6-B7"],
            MahjongGame.ClaimCandidates(table.State, discard, 0, 1).Select(c => c.Describe(discard)));

        Assert.Equal(
            ["Kang B5", "Pung B5"],
            MahjongGame.ClaimCandidates(table.State, discard, 0, 3).Select(c => c.Describe(discard)));
    }
}
