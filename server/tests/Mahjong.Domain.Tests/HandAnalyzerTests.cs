using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

public class HandAnalyzerTests
{
    private static readonly ExposedMeld[] NoMelds = [];

    private static WinAnalysis Analyze(string notation, Tile? joker = null, RuleOptions? rules = null)
        => HandAnalyzer.Analyze(TileNotation.Parse(notation), NoMelds, joker, rules);

    // ---------------------------------------------------------------- shape of the tile set

    [Fact]
    public void CanonicalSet_has_144_tiles_split_136_playable_and_8_bonus()
    {
        Assert.Equal(144, TileSet.Canonical.Count);
        Assert.Equal(136, TileSet.Canonical.Count(t => t.IsPlayable));
        Assert.Equal(8, TileSet.Canonical.Count(t => t.IsBonus));
    }

    [Fact]
    public void CanonicalSet_has_exactly_four_copies_of_every_playable_face()
    {
        var byFace = TileSet.Canonical.Where(t => t.IsPlayable).GroupBy(t => t);

        Assert.Equal(34, byFace.Count());
        Assert.All(byFace, g => Assert.Equal(4, g.Count()));
    }

    [Fact]
    public void CanonicalSet_split_is_108_suited_16_winds_12_dragons_4_flowers_4_seasons()
    {
        Assert.Equal(108, TileSet.Canonical.Count(t => t.IsSuited));
        Assert.Equal(16, TileSet.Canonical.Count(t => t.Suit == Suit.Wind));
        Assert.Equal(12, TileSet.Canonical.Count(t => t.Suit == Suit.Dragon));
        Assert.Equal(4, TileSet.Canonical.Count(t => t.Suit == Suit.Flower));
        Assert.Equal(4, TileSet.Canonical.Count(t => t.Suit == Suit.Season));
    }

    [Fact]
    public void Playable_ids_come_first_so_id_under_136_means_playable()
    {
        for (var id = 0; id < TileSet.TotalTiles; id++)
            Assert.Equal(id < TileSet.PlayableTiles, TileSet.Canonical[id].IsPlayable);
    }

    [Fact]
    public void PlayableIndex_round_trips_for_every_playable_face()
    {
        for (var index = 0; index < TileSet.PlayableFaces; index++)
            Assert.Equal(index, Tile.FromPlayableIndex(index).PlayableIndex);
    }

    // ---------------------------------------------------------------- standard todas

    [Theory]
    // five chows plus a pair
    [InlineData("123d 456d 789d 123b 456b 99c")]
    // five pungs plus a pair
    [InlineData("111d 222d 333d 111b 222b 99c")]
    // mixed, pair in a third suit
    [InlineData("123d 456d 789d 111b 222b 33c")]
    // the pair sits inside a run of the same suit
    [InlineData("111d 123d 123d 123d 456b 22b")]
    public void Complete_17_tile_hands_are_a_win(string hand)
    {
        var tiles = TileNotation.Parse(hand);
        Assert.Equal(17, tiles.Count);

        Assert.True(Analyze(hand).IsWin, $"expected a win: {hand}");
    }

    [Theory]
    // 17 tiles but the last two are not a pair
    [InlineData("123d 456d 789d 111b 222b 34c")]
    // 17 tiles, one bahay short because 1-3-5 is not a run
    [InlineData("135d 456d 789d 111b 222b 33c")]
    // two pairs and only four bahay
    [InlineData("123d 456d 789d 111b 22b 33c")]
    // 16 tiles: not enough to win on
    [InlineData("123d 456d 789d 111b 222b 3c")]
    public void Incomplete_hands_are_not_a_win(string hand)
    {
        Assert.False(Analyze(hand).IsWin, $"expected no win: {hand}");
    }

    [Fact]
    public void Four_identical_tiles_left_undeclared_cannot_complete_a_hand()
    {
        // 1111 bamboo is a pung plus one stranded tile. Winning on four of a kind requires
        // declaring a kang, which moves the tiles out of the concealed hand.
        Assert.False(Analyze("123d 456d 789d 1111b 22c").IsWin);
    }

    [Fact]
    public void A_hand_with_a_bonus_tile_in_it_is_rejected_rather_than_silently_scored()
    {
        // Flowers and seasons are the only bonus tiles, and they are exposed the moment they are
        // drawn, so one can never be sitting in the concealed hand.
        var ex = Assert.Throws<ArgumentException>(() => Analyze("123d 456d 789d 111b 222b 3c 1f"));
        Assert.Contains("bonus tile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- winds and dragons

    [Theory]
    // a pung of east, and a pung of red dragon
    [InlineData("123d 456d 789d 111b 111w 22c")]
    [InlineData("123d 456d 789d 111b 111r 22c")]
    // honors as the pair
    [InlineData("123d 456d 789d 111b 222b 11w")]
    [InlineData("123d 456d 789d 111b 222b 33r")]
    // a hand of nothing but honors, five pungs and a pair
    [InlineData("111w 222w 333w 444w 111r 22r")]
    public void Winds_and_dragons_form_pungs_and_pairs(string notation)
        => Assert.True(Analyze(notation).IsWin);

    [Theory]
    // east-south-west is not a run
    [InlineData("123d 456d 789d 111b 123w 22c")]
    // red-green-white is not a run either
    [InlineData("123d 456d 789d 111b 123r 22c")]
    // and a run can never walk off the end of the dragons into nothing
    [InlineData("123d 456d 789d 111b 234w 22c")]
    public void Winds_and_dragons_never_form_a_run(string notation)
        => Assert.False(Analyze(notation).IsWin);

    [Fact]
    public void A_wind_can_be_the_joker_face()
    {
        // South is the joker, so the three souths held stand in for the fifth bahay.
        Assert.True(Analyze("123d 456d 111b 222b 22c 222w", Tile.Parse("W2")).IsWin);

        // Without the joker rule the same tiles are four bahay, a pair and a loose pung of souths,
        // which is one bahay short of a hand.
        Assert.False(Analyze("123d 456d 111b 222b 22c 22w").IsWin);
    }

    // ---------------------------------------------------------------- exposed melds

    [Fact]
    public void Exposed_melds_reduce_how_many_bahay_the_concealed_tiles_must_supply()
    {
        var pung = new ExposedMeld(SetKind.Pung, TileNotation.ParseRefs("111b"), ClaimedFromSeat: 2);
        var chow = new ExposedMeld(SetKind.Chow, TileNotation.ParseRefs("456b"), ClaimedFromSeat: 1);

        // Two melds on the table, so the 11 concealed tiles only need to make three bahay + pair.
        var concealed = TileNotation.Parse("123d 456d 789d 99c");
        Assert.Equal(11, concealed.Count);

        var result = HandAnalyzer.Analyze(concealed, [pung, chow], joker: null);
        Assert.True(result.IsWin);
        Assert.All(result.Readings, r => Assert.Equal(6, r.Sets.Count));
    }

    [Fact]
    public void A_kang_counts_as_one_bahay_not_two()
    {
        var kang = new ExposedMeld(SetKind.Kang, TileNotation.ParseRefs("1111b"), ClaimedFromSeat: 3);

        // One kang on the table leaves four bahay plus the pair to come from 14 concealed tiles.
        var concealed = TileNotation.Parse("123d 456d 789d 123c 99c");
        Assert.Equal(14, concealed.Count);

        Assert.True(HandAnalyzer.Analyze(concealed, [kang], joker: null).IsWin);
    }

    // ---------------------------------------------------------------- siete pares

    [Fact]
    public void Siete_pares_is_seven_pairs_plus_one_bahay()
    {
        var hand = "11d 22d 33d 44d 55d 66d 77d 111b";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        var result = Analyze(hand);
        Assert.True(result.IsWin);
        Assert.Contains(result.Readings, r => r.IsSietePares);
    }

    [Fact]
    public void Seven_pairs_without_the_extra_bahay_is_only_fourteen_tiles_and_does_not_win()
    {
        Assert.False(Analyze("11d 22d 33d 44d 55d 66d 77d").IsWin);
    }

    [Fact]
    public void Siete_pares_rejects_a_repeated_pair_when_the_table_requires_seven_distinct_faces()
    {
        // 1111d read as "two pairs of 1 dot" is refused under the default rule.
        var hand = "1111d 22d 33d 44d 55d 66d 111b";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        Assert.DoesNotContain(Analyze(hand).Readings, r => r.IsSietePares);

        var permissive = RuleOptions.Default with { DistinctPairsForSietePares = false };
        Assert.Contains(Analyze(hand, rules: permissive).Readings, r => r.IsSietePares);
    }

    [Fact]
    public void Siete_pares_can_be_switched_off_for_a_table()
    {
        // Pairs spaced three apart, so no run can ever be formed out of them. This matters:
        // "11d 22d 33d 44d 55d 66d 77d" is ALSO a standard win, read as 123d 123d 456d 456d 77d,
        // so it cannot be used to test that siete pares is switched off.
        const string hand = "11d 44d 77d 11b 44b 77b 11c 999c";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        var off = RuleOptions.Default with { SieteParesEnabled = false };

        Assert.True(Analyze(hand).IsWin);
        Assert.False(Analyze(hand, rules: off).IsWin);
    }

    // ---------------------------------------------------------------- jokers

    [Fact]
    public void A_joker_fills_the_gap_in_an_otherwise_incomplete_run()
    {
        // 4-6 dots is missing its 5. The lone 9-chars is the joker face this hand, so it is wild.
        const string hand = "123d 46d 789d 111b 222b 33c 9c";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        Assert.False(Analyze(hand).IsWin);
        Assert.True(Analyze(hand, joker: Tile.Parse("C9")).IsWin);
    }

    [Fact]
    public void Two_jokers_can_complete_two_different_groups()
    {
        // Missing the 5 dots and the third 2 bamboo; two 9-char jokers cover both.
        const string hand = "123d 46d 789d 111b 22b 33c 99c";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        Assert.True(Analyze(hand, joker: Tile.Parse("C9")).IsWin);
    }

    [Fact]
    public void A_joker_can_stand_in_for_half_of_the_pair()
    {
        const string hand = "123d 456d 789d 111b 222b 3c 9c";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        Assert.False(Analyze(hand).IsWin);
        Assert.True(Analyze(hand, joker: Tile.Parse("C9")).IsWin);
    }

    [Fact]
    public void Joker_tiles_are_never_counted_as_their_own_face()
    {
        // With 3 dots as the joker, the "33c ... 3d" reading is irrelevant: the 3 dots are wild.
        // This hand is short a tile for the 1-dot pung and the joker covers it.
        const string hand = "11d 3d 456d 789d 111b 222b 33c";
        Assert.Equal(17, TileNotation.Parse(hand).Count);

        Assert.True(Analyze(hand, joker: Tile.Parse("D3")).IsWin);
    }

    // ---------------------------------------------------------------- waits

    [Fact]
    public void A_single_wait_reports_exactly_one_winning_tile()
    {
        // 16 tiles, five bahay complete, holding one half of the pair.
        var concealed = TileNotation.Parse("123d 456d 789d 111b 222b 9c");
        Assert.Equal(16, concealed.Count);

        var winners = HandAnalyzer.WinningTiles(concealed, NoMelds, joker: null);
        Assert.Equal([Tile.Parse("C9")], winners);
    }

    [Fact]
    public void A_paningit_wait_reports_the_single_middle_tile_of_the_run()
    {
        // Holding 4 and 6 dots, needing the 5.
        var concealed = TileNotation.Parse("123d 46d 789d 111b 222b 33c");
        Assert.Equal(16, concealed.Count);

        var winners = HandAnalyzer.WinningTiles(concealed, NoMelds, joker: null);
        Assert.Equal([Tile.Parse("D5")], winners);
    }

    [Fact]
    public void A_back_to_back_wait_reports_both_pair_tiles()
    {
        // 123d 456d 789d 111b + 22b + 33c: either 2b or 3c completes the hand.
        var concealed = TileNotation.Parse("123d 456d 789d 111b 22b 33c");
        Assert.Equal(16, concealed.Count);

        var winners = HandAnalyzer.WinningTiles(concealed, NoMelds, joker: null).OrderBy(t => t.Code).ToList();
        Assert.Equal([Tile.Parse("B2"), Tile.Parse("C3")], winners);
    }

    [Fact]
    public void An_open_run_waits_on_both_ends()
    {
        // 45 dots waits on 3 or 6.
        var concealed = TileNotation.Parse("123d 45d 789d 111b 222b 33c");
        Assert.Equal(16, concealed.Count);

        var winners = HandAnalyzer.WinningTiles(concealed, NoMelds, joker: null).OrderBy(t => t.Rank).ToList();
        Assert.Equal([Tile.Parse("D3"), Tile.Parse("D6")], winners);
    }

    [Fact]
    public void A_tile_already_visible_four_times_is_never_reported_as_a_winning_tile()
    {
        // Three 9-chars are melded and the fourth is in hand, so pairing the 9-chars would need
        // a fifth copy. The hand looks one tile short but is in fact dead.
        var pung = new ExposedMeld(SetKind.Pung, TileNotation.ParseRefs("999c"), ClaimedFromSeat: 1);
        var concealed = TileNotation.Parse("123d 456d 789d 111b 9c");
        Assert.Equal(13, concealed.Count);

        var winners = HandAnalyzer.WinningTiles(concealed, [pung], joker: null);

        Assert.DoesNotContain(Tile.Parse("C9"), winners);
        Assert.Empty(winners);
    }

    // ---------------------------------------------------------------- readings

    [Fact]
    public void An_ambiguous_hand_is_reported_with_every_reading_so_the_scorer_can_choose()
    {
        // 111d 234d can be read as (pung of 1d)(234d) or as (123d)(1d 4d ...) depending on
        // what surrounds it. Here 111 222 333 dots reads as three pungs or as three 123 chows.
        var result = Analyze("111d 222d 333d 111b 222b 99c");

        Assert.True(result.IsWin);
        Assert.True(result.Readings.Count > 1, "expected more than one reading");
        Assert.Contains(result.Readings, r => r.Bahay.All(s => s.Kind == SetKind.Pung));
        Assert.Contains(result.Readings, r => r.Bahay.Count(s => s.Kind == SetKind.Chow) == 3);
    }

    [Fact]
    public void Readings_are_deduplicated()
    {
        var result = Analyze("123d 456d 789d 123b 456b 99c");

        var keys = result.Readings
            .Select(r => string.Join(",", r.Sets.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal)))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Every_reading_of_a_standard_win_has_five_bahay_and_one_pair()
    {
        var result = Analyze("123d 456d 789d 111b 222b 33c");

        Assert.All(result.Readings, r =>
        {
            Assert.Single(r.Sets, s => s.Kind == SetKind.Pair);
            Assert.Equal(5, r.Bahay.Count());
        });
    }
}
