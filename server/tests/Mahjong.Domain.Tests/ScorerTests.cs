using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

public class ScorerTests
{
    private static readonly ExposedMeld[] NoMelds = [];

    /// <summary>
    /// Builds a win where the last tile of <paramref name="fullHand"/> is treated as the tile
    /// that completed the hand.
    /// </summary>
    private static WinInput Win(
        string fullHand,
        Tile winningTile,
        bool selfDrawn = false,
        int discardCount = 20,
        IReadOnlyList<ExposedMeld>? melds = null,
        Tile? joker = null,
        bool bisaklat = false)
    {
        var tiles = TileNotation.Parse(fullHand).ToList();

        var index = tiles.FindIndex(t => t == winningTile);
        Assert.True(index >= 0, $"{winningTile} is not in {fullHand}");
        tiles.RemoveAt(index);

        return new WinInput(tiles, melds ?? NoMelds, winningTile, selfDrawn, discardCount, joker, bisaklat);
    }

    // ---------------------------------------------------------------- base and multipliers

    [Fact]
    public void A_plain_win_off_a_discard_pays_the_base_plus_whatever_bonuses_apply()
    {
        // 123d 456d 789d 111b 222b 33c won on the 3 chars. Late in the hand, nothing exposed.
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3")));

        Assert.Equal(2, score.BaseUnits);
        // Fully concealed hand, and the wait was on the pair only.
        Assert.Contains(WinBonus.Concealed, score.Bonuses.Keys);
        Assert.Equal(score.BaseUnits + score.BonusUnits, score.TotalUnits);
    }

    [Fact]
    public void Bunot_doubles_the_whole_total()
    {
        const string hand = "123d 456d 789d 111b 222b 33c";
        var claimed = Scorer.Score(Win(hand, Tile.Parse("C3"), selfDrawn: false));
        var drawn = Scorer.Score(Win(hand, Tile.Parse("C3"), selfDrawn: true));

        Assert.Equal(claimed.TotalUnits * 2, drawn.TotalUnits);
    }

    [Fact]
    public void Bunot_doubling_can_be_switched_off()
    {
        var rules = RuleOptions.Default with { Scoring = ScoringProfile.Default with { BunotDoubles = false } };
        const string hand = "123d 456d 789d 111b 222b 33c";

        var claimed = Scorer.Score(Win(hand, Tile.Parse("C3")), rules);
        var drawn = Scorer.Score(Win(hand, Tile.Parse("C3"), selfDrawn: true), rules);

        Assert.Equal(claimed.TotalUnits, drawn.TotalUnits);
    }

    // ---------------------------------------------------------------- named hands

    [Fact]
    public void Escalera_is_awarded_for_a_full_one_to_nine_run_in_one_suit()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3")));
        Assert.Contains(WinBonus.Escalera, score.Bonuses.Keys);
        Assert.Equal(4, score.Bonuses[WinBonus.Escalera]);
    }

    [Fact]
    public void Escalera_is_not_awarded_when_the_runs_are_spread_across_suits()
    {
        var score = Scorer.Score(Win("123d 456b 789c 111b 222b 33c", Tile.Parse("C3")));
        Assert.DoesNotContain(WinBonus.Escalera, score.Bonuses.Keys);
    }

    [Fact]
    public void Flush_is_awarded_when_every_tile_is_the_same_suit()
    {
        // All dots: 123 456 789 111 222 + 99 pair.
        var score = Scorer.Score(Win("123d 456d 789d 111d 222d 99d", Tile.Parse("D9")));
        Assert.Contains(WinBonus.Flush, score.Bonuses.Keys);
    }

    [Fact]
    public void Flush_is_not_awarded_when_the_pair_is_in_another_suit()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111d 222d 99c", Tile.Parse("C9")));
        Assert.DoesNotContain(WinBonus.Flush, score.Bonuses.Keys);
    }

    [Fact]
    public void All_pungs_is_awarded_when_every_bahay_is_a_pung()
    {
        var score = Scorer.Score(Win("111d 222d 333d 111b 222b 99c", Tile.Parse("C9")));
        Assert.Contains(WinBonus.AllPungs, score.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.AllChows, score.Bonuses.Keys);
    }

    [Fact]
    public void All_chows_is_awarded_when_every_bahay_is_a_run()
    {
        var score = Scorer.Score(Win("123d 456d 789d 123b 456b 99c", Tile.Parse("C9")));
        Assert.Contains(WinBonus.AllChows, score.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.AllPungs, score.Bonuses.Keys);
    }

    [Fact]
    public void Siete_pares_is_awarded_and_scored_from_the_seven_pairs_reading()
    {
        // Pairs spaced three apart so the hand cannot also be read as runs.
        var score = Scorer.Score(Win("11d 44d 77d 11b 44b 77b 11c 999c", Tile.Parse("C1")));

        Assert.True(score.Reading.IsSietePares);
        Assert.Contains(WinBonus.SietePares, score.Bonuses.Keys);
    }

    [Fact]
    public void Bisaklat_replaces_every_other_bonus()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3"), bisaklat: true));

        Assert.Equal([WinBonus.Bisaklat], score.Bonuses.Keys);
        Assert.Equal(2 + 20, score.TotalUnits);
    }

    // ---------------------------------------------------------------- concealed vs exposed

    [Fact]
    public void A_hand_with_nothing_on_the_table_counts_as_concealed()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3")));
        Assert.Contains(WinBonus.Concealed, score.Bonuses.Keys);
    }

    [Fact]
    public void A_secret_kang_does_not_break_a_concealed_hand()
    {
        var secret = new ExposedMeld(SetKind.Kang, TileNotation.ParseRefs("1111b"), Concealed: true);
        var input = Win("123d 456d 789d 123c 99c", Tile.Parse("C9"), melds: [secret]);

        var score = Scorer.Score(input);
        Assert.Contains(WinBonus.Concealed, score.Bonuses.Keys);
    }

    [Fact]
    public void A_claimed_pung_does_break_a_concealed_hand()
    {
        var claimed = new ExposedMeld(SetKind.Pung, TileNotation.ParseRefs("111b"), ClaimedFromSeat: 2);
        var input = Win("123d 456d 789d 222b 33c", Tile.Parse("C3"), melds: [claimed]);

        var score = Scorer.Score(input);
        Assert.DoesNotContain(WinBonus.Concealed, score.Bonuses.Keys);
    }

    [Fact]
    public void All_exposed_is_awarded_when_all_five_bahay_are_on_the_table()
    {
        ExposedMeld Meld(string n) => new(SetKind.Chow, TileNotation.ParseRefs(n), ClaimedFromSeat: 1);

        var melds = new[]
        {
            Meld("123d"), Meld("456d"), Meld("789d"), Meld("123b"), Meld("456b"),
        };

        var score = Scorer.Score(Win("99c", Tile.Parse("C9"), melds: melds));

        Assert.Contains(WinBonus.AllExposed, score.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.Concealed, score.Bonuses.Keys);
    }

    // ---------------------------------------------------------------- waits

    [Fact]
    public void Waiting_on_one_tile_to_finish_the_pair_is_a_single_wait()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 99c", Tile.Parse("C9")));

        Assert.Single(score.Wait);
        Assert.Contains(WinBonus.Single, score.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.Paningit, score.Bonuses.Keys);
    }

    [Fact]
    public void Waiting_on_the_middle_of_a_run_is_paningit_and_supersedes_single()
    {
        // Held 4 and 6 dots, won on the 5.
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("D5")));

        Assert.Single(score.Wait);
        Assert.Contains(WinBonus.Paningit, score.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.Single, score.Bonuses.Keys);
    }

    [Fact]
    public void Waiting_on_either_of_two_pairs_is_back_to_back()
    {
        // 123d 456d 789d 111b + 22b + 33c: either the 2 bamboo or the 3 chars finishes it.
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("B2")));

        Assert.Equal(2, score.Wait.Count);
        Assert.Contains(WinBonus.BackToBack, score.Bonuses.Keys);
    }

    [Fact]
    public void A_win_within_the_first_five_discards_is_a_quick_win()
    {
        var quick = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3"), discardCount: 4));
        var late = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3"), discardCount: 40));

        Assert.Contains(WinBonus.QuickWin, quick.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.QuickWin, late.Bonuses.Keys);
    }

    // ---------------------------------------------------------------- best reading

    [Fact]
    public void The_best_paying_reading_is_the_one_that_is_scored()
    {
        // 111d 222d 333d reads as three pungs or as three 123 chows. All-pungs pays 2, all-chows
        // pays 1, so the pung reading has to win.
        var score = Scorer.Score(Win("111d 222d 333d 111b 222b 99c", Tile.Parse("C9")));

        Assert.Contains(WinBonus.AllPungs, score.Bonuses.Keys);
        Assert.DoesNotContain(WinBonus.AllChows, score.Bonuses.Keys);
    }

    [Fact]
    public void Scoring_a_hand_that_is_not_a_win_is_an_error_rather_than_a_zero()
    {
        var input = new WinInput(
            TileNotation.Parse("123d 456d 789d 111b 222b 3c"),
            NoMelds,
            Tile.Parse("C7"),
            SelfDrawn: false,
            DiscardCount: 10);

        Assert.Throws<ArgumentException>(() => Scorer.Score(input));
    }

    // ---------------------------------------------------------------- settlement

    [Fact]
    public void Winning_off_a_discard_charges_the_discarder_double_and_the_others_flat()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3")));
        var settlements = Scorer.Settle(score, winnerSeat: 0, discarderSeat: 2);

        var total = score.TotalUnits;

        Assert.Equal(total * 4, settlements.Single(s => s.Seat == 0).Delta);
        Assert.Equal(-total, settlements.Single(s => s.Seat == 1).Delta);
        Assert.Equal(-total * 2, settlements.Single(s => s.Seat == 2).Delta);
        Assert.Equal(-total, settlements.Single(s => s.Seat == 3).Delta);
    }

    [Fact]
    public void Winning_off_the_wall_charges_all_three_the_same_doubled_amount()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3"), selfDrawn: true));
        var settlements = Scorer.Settle(score, winnerSeat: 1, discarderSeat: null);

        var total = score.TotalUnits;

        Assert.Equal(total * 3, settlements.Single(s => s.Seat == 1).Delta);
        foreach (var seat in new[] { 0, 2, 3 })
            Assert.Equal(-total, settlements.Single(s => s.Seat == seat).Delta);
    }

    [Fact]
    public void Every_settlement_sums_to_zero_so_no_money_is_created_or_destroyed()
    {
        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3")));

        Assert.Equal(0, Scorer.Settle(score, 0, 2).Sum(s => s.Delta));
        Assert.Equal(0, Scorer.Settle(score, 0, null).Sum(s => s.Delta));
        Assert.Equal(0, Scorer.SettleAmbition(Ambition.SecretKang, 3).Sum(s => s.Delta));
    }

    [Theory]
    [InlineData(Ambition.NoFlowers, 1)]
    [InlineData(Ambition.Kang, 1)]
    [InlineData(Ambition.SecretKang, 2)]
    [InlineData(Ambition.Sagasa, 2)]
    public void An_ambition_collects_its_value_from_each_of_the_other_three_seats(Ambition ambition, int units)
    {
        var settlements = Scorer.SettleAmbition(ambition, claimantSeat: 2);

        Assert.Equal(units * 3, settlements.Single(s => s.Seat == 2).Delta);
        foreach (var seat in new[] { 0, 1, 3 })
            Assert.Equal(-units, settlements.Single(s => s.Seat == seat).Delta);
    }

    [Fact]
    public void A_table_can_change_any_value_without_a_code_change()
    {
        var houseRules = RuleOptions.Default with
        {
            Scoring = ScoringProfile.Default with
            {
                TodasBase = 10,
                DiscarderMultiplier = 3,
                Bonuses = new Dictionary<WinBonus, int>(ScoringProfile.Default.Bonuses)
                {
                    [WinBonus.Escalera] = 25,
                },
            },
        };

        var score = Scorer.Score(Win("123d 456d 789d 111b 222b 33c", Tile.Parse("C3")), houseRules);

        Assert.Equal(10, score.BaseUnits);
        Assert.Equal(25, score.Bonuses[WinBonus.Escalera]);

        var settlements = Scorer.Settle(score, 0, 1, houseRules);
        Assert.Equal(-score.TotalUnits * 3, settlements.Single(s => s.Seat == 1).Delta);
    }
}
