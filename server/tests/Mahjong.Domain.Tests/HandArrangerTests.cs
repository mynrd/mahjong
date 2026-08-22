using System.Diagnostics;
using Mahjong.Domain;

namespace Mahjong.Domain.Tests;

/// <summary>
/// <see cref="HandArranger"/> reads a hand as blocks for the Auto Arrange layout. It never decides
/// a move, so what is being pinned here is the ordering in section 6.4 of the plan: most complete
/// sets, then a pair, then the partials that could still be used, then jokers left unspent - and
/// that the same hand always comes back the same way round.
/// </summary>
public class HandArrangerTests
{
    private static IReadOnlyList<HandGroup> Arrange(
        string notation, Tile? joker = null, IReadOnlyList<ExposedMeld>? melds = null) =>
        HandArranger.Arrange(
            TileNotation.ParseRefs(notation),
            melds ?? [],
            joker,
            joker is null ? RuleOptions.Default with { JokerEnabled = false } : RuleOptions.Default);

    private static string Faces(HandGroup group) =>
        string.Join(" ", group.Tiles.Select(t => t.Tile.Code));

    private static string[] Needs(HandGroup group) => group.Needs.Select(t => t.Code).ToArray();

    // ---------------------------------------------------------------- how the hand is read

    [Fact]
    public void A_hand_that_reads_two_ways_takes_the_reading_with_the_pair()
    {
        // B1 B1 B1 B2 B3 is either a pung of 1s next to a floating 2-3, or a 1-2-3 chow next to a
        // pair of 1s. Both have one set, so rule 2 decides.
        var groups = Arrange("11123b");

        Assert.Equal([HandGroupKind.Chow, HandGroupKind.Pair], groups.Select(g => g.Kind));
        Assert.Equal("B1 B2 B3", Faces(groups[0]));
        Assert.Equal("B1 B1", Faces(groups[1]));
    }

    [Fact]
    public void Four_of_a_face_is_one_kang_rather_than_a_pung_with_a_spare()
    {
        var groups = Arrange("4444c");

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Kang, only.Kind);
        Assert.Equal(4, only.Tiles.Count);
    }

    [Fact]
    public void Two_adjacent_tiles_are_a_partial_that_names_both_ends()
    {
        var groups = Arrange("34b");

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Partial, only.Kind);
        Assert.Equal(["B2", "B5"], Needs(only));
    }

    [Fact]
    public void A_one_gap_run_is_a_partial_that_names_the_middle()
    {
        var groups = Arrange("35b");

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Partial, only.Kind);
        Assert.Equal(["B4"], Needs(only));
    }

    [Fact]
    public void A_partial_at_the_end_of_a_suit_only_names_the_end_that_exists()
    {
        var groups = Arrange("12b");

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Partial, only.Kind);
        Assert.Equal(["B3"], Needs(only));
    }

    [Fact]
    public void A_run_never_crosses_a_suit_boundary()
    {
        // 9 bamboo and 1 dots are adjacent in the tile ordering but not in the game.
        var groups = Arrange("9b 1d");

        Assert.Equal([HandGroupKind.Floater, HandGroupKind.Floater], groups.Select(g => g.Kind));
        Assert.Equal(["D1", "B9"], groups.Select(Faces));
    }

    [Fact]
    public void A_complete_hand_reads_as_five_sets_and_a_pair_with_nothing_left_over()
    {
        var groups = Arrange("123d 456d 789d 111b 999c 22c");

        Assert.Equal(6, groups.Count);
        Assert.Equal(5, groups.Count(g => g.Kind is HandGroupKind.Chow or HandGroupKind.Pung or HandGroupKind.Kang));
        Assert.Single(groups, g => g.Kind == HandGroupKind.Pair);
        Assert.DoesNotContain(groups, g => g.Kind is HandGroupKind.Partial or HandGroupKind.Floater);
    }

    [Fact]
    public void Exposed_melds_count_toward_the_five_sets()
    {
        // Three melds down already, so only two sets are still wanted. The concealed 1-1-1-2-3
        // still reads chow-plus-pair, and the cap on useful partials has moved with the melds.
        var melds = new[]
        {
            new ExposedMeld(SetKind.Pung, TileNotation.ParseRefs("555d")),
            new ExposedMeld(SetKind.Chow, TileNotation.ParseRefs("678d")),
            new ExposedMeld(SetKind.Pung, TileNotation.ParseRefs("333c")),
        };

        var groups = Arrange("11123b", melds: melds);

        Assert.Equal([HandGroupKind.Chow, HandGroupKind.Pair], groups.Select(g => g.Kind));
    }

    [Fact]
    public void Blocks_come_back_in_layout_order_sets_then_pair_then_partials_then_floaters()
    {
        var groups = Arrange("444c 123b 55d 23d 89d 78b 67c 9d");

        Assert.Equal(
            [
                HandGroupKind.Pung,
                HandGroupKind.Chow,
                HandGroupKind.Pair,
                HandGroupKind.Partial,
                HandGroupKind.Partial,
                HandGroupKind.Partial,
                HandGroupKind.Partial,
                HandGroupKind.Floater,
            ],
            groups.Select(g => g.Kind));

        Assert.Equal("C4 C4 C4", Faces(groups[0]));
        Assert.Equal("B1 B2 B3", Faces(groups[1]));
        Assert.Equal("D5 D5", Faces(groups[2]));
        Assert.Equal("D9", Faces(groups[7]));
    }

    [Fact]
    public void The_same_hand_arranged_twice_comes_back_identical()
    {
        const string hand = "23d 55d 89d 123b 78b 444c 67c 9d";

        var first = Arrange(hand);
        var second = Arrange(hand);

        Assert.Equal(
            first.Select(g => $"{g.Kind}:{Faces(g)}"),
            second.Select(g => $"{g.Kind}:{Faces(g)}"));

        // And the order does not depend on the order the tiles were handed in.
        var shuffled = Arrange("9d 67c 78b 444c 123b 89d 55d 23d");

        Assert.Equal(
            first.Select(g => $"{g.Kind}:{TileNotation.Format(g.Tiles)}"),
            shuffled.Select(g => $"{g.Kind}:{TileNotation.Format(g.Tiles)}"));
    }

    // ---------------------------------------------------------------- jokers

    [Fact]
    public void A_joker_fills_the_group_it_completes()
    {
        // C9 is wild this hand, so the C9 tile plus two 5-dots is a pung of 5-dots.
        var groups = Arrange("55d 9c", joker: Tile.Parse("C9"));

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Pung, only.Kind);
        Assert.Equal(1, only.JokersUsed);
        Assert.Equal(3, only.Tiles.Count);
    }

    [Fact]
    public void A_joker_inside_a_run_sits_where_it_stands_in()
    {
        var groups = Arrange("35b 9c", joker: Tile.Parse("C9"));

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Chow, only.Kind);
        Assert.Equal("B3 C9 B5", Faces(only));
        Assert.Equal(1, only.JokersUsed);
    }

    [Fact]
    public void A_joker_that_completes_nothing_comes_back_as_its_own_floater()
    {
        var groups = Arrange("9c", joker: Tile.Parse("C9"));

        var only = Assert.Single(groups);
        Assert.Equal(HandGroupKind.Floater, only.Kind);
        Assert.Equal("C9", Faces(only));
        Assert.Equal(0, only.JokersUsed);
    }

    [Fact]
    public void A_joker_is_not_spent_where_a_real_tile_would_do()
    {
        // 5-5-5 dots is already a pung. Spending the joker on it as a kang would cost a joker for
        // nothing, so it stays out - rule 4.
        var groups = Arrange("555d 9c", joker: Tile.Parse("C9"));

        Assert.Equal([HandGroupKind.Pung, HandGroupKind.Floater], groups.Select(g => g.Kind));
        Assert.Equal(0, groups.Sum(g => g.JokersUsed));
    }

    // ---------------------------------------------------------------- the safety valve

    [Fact]
    public void A_hand_where_nothing_groups_reads_as_all_floaters()
    {
        // Nine is the most floaters a hand can hold: every tile has to be three ranks or more from
        // its neighbours and no face can repeat, which leaves ranks 1, 4 and 7 of each suit.
        var groups = Arrange("147d 147b 147c");

        Assert.Equal(9, groups.Count);
        Assert.All(groups, g => Assert.Equal(HandGroupKind.Floater, g.Kind));
    }

    [Fact]
    public void A_full_hand_of_overlapping_shapes_still_arranges_promptly()
    {
        // The worst realistic case for the search: seventeen tiles where nearly every one could
        // belong to two or three different groups.
        var watch = Stopwatch.StartNew();
        var groups = Arrange("123345d 234456b 22345c");
        watch.Stop();

        Assert.Equal(17, groups.Sum(g => g.Tiles.Count));
        Assert.True(watch.ElapsedMilliseconds < 500, $"Took {watch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void Every_tile_handed_in_comes_back_exactly_once()
    {
        const string hand = "23d 55d 89d 123b 78b 444c 67c 9d";

        var given = TileNotation.ParseRefs(hand).Select(t => t.Id).Order();
        var back = Arrange(hand).SelectMany(g => g.Tiles).Select(t => t.Id).Order();

        Assert.Equal(given, back);
    }

    // ---------------------------------------------------------------- laid out as it won

    /// <summary>
    /// Lays a winning hand out by the reading the scorer picked, which is the path the last frame
    /// of a replay takes. The last tile of <paramref name="notation"/> is treated as the winning
    /// tile, so the scorer is handed the same shape the game hands it.
    /// </summary>
    private static IReadOnlyList<HandGroup> FromWin(
        string notation, Tile? joker = null, IReadOnlyList<ExposedMeld>? melds = null)
    {
        var concealed = TileNotation.ParseRefs(notation);
        melds ??= [];

        var rules = joker is null ? RuleOptions.Default with { JokerEnabled = false } : RuleOptions.Default;

        var score = Scorer.Score(
            new WinInput(
                concealed.Take(concealed.Count - 1).Select(t => t.Tile).ToList(),
                melds,
                concealed[^1].Tile,
                SelfDrawn: true,
                DiscardCount: 20,
                joker),
            rules);

        return HandArranger.FromReading(score.Reading, concealed, melds, joker);
    }

    [Fact]
    public void A_joker_sits_in_the_slot_it_stands_in_for()
    {
        // Room 4XAUZ9 hand 1, the hand that made this worth pinning: two 8-dot jokers standing in
        // for B2 and C4, which reads as a broken hand until they are put where they belong.
        var groups = FromWin("1344999b 22334678c 88d", Tile.Parse("D8"));

        Assert.Equal(17, groups.Sum(g => g.Tiles.Count));
        Assert.Equal(2, groups.Sum(g => g.JokersUsed));

        // The joker is inside a chow, in the slot for the face it is playing as - never dangling
        // off the end, and never left over as a floater.
        foreach (var group in groups.Where(g => g.JokersUsed > 0))
            Assert.Equal(HandGroupKind.Chow, group.Kind);

        Assert.Equal("B1 D8 B3", Faces(groups.Single(g => Faces(g).StartsWith("B1"))));
        Assert.Equal("C2 C3 D8", Faces(groups.Single(g => g.Kind == HandGroupKind.Chow && g.JokersUsed == 1 && Faces(g).StartsWith("C2"))));
        Assert.Equal("B9 B9 B9", Faces(groups.Single(g => g.Kind == HandGroupKind.Pung)));
        Assert.Equal("B4 B4", Faces(groups.Single(g => g.Kind == HandGroupKind.Pair)));
    }

    [Fact]
    public void A_siete_pares_win_is_laid_out_as_seven_pairs_rather_than_as_runs()
    {
        // The case Arrange gets wrong for a finished hand: it reads the ten dots as runs, so the
        // hand does not show the shape that actually paid.
        const string hand = "1122334455d 6677b 111c";

        Assert.Contains(Arrange(hand), g => g.Kind == HandGroupKind.Chow);

        var won = FromWin(hand);

        Assert.Equal(7, won.Count(g => g.Kind == HandGroupKind.Pair));
        Assert.DoesNotContain(won, g => g.Kind == HandGroupKind.Chow);
        Assert.Equal("C1 C1 C1", Faces(won.Single(g => g.Kind == HandGroupKind.Pung)));
    }

    [Fact]
    public void Exposed_melds_are_counted_off_the_front_and_not_laid_out_again()
    {
        var melds = new ExposedMeld[]
        {
            new(SetKind.Chow, TileNotation.ParseRefs("123d"), ClaimedFromSeat: 3),
            new(SetKind.Pung, TileNotation.ParseRefs("555b")),
        };

        var groups = FromWin("456d 789d 234c 99c", melds: melds);

        Assert.Equal(11, groups.Sum(g => g.Tiles.Count));
        Assert.DoesNotContain(groups, g => Faces(g) == "D1 D2 D3");
        Assert.DoesNotContain(groups, g => Faces(g) == "B5 B5 B5");
        Assert.Equal(
            [HandGroupKind.Chow, HandGroupKind.Chow, HandGroupKind.Chow, HandGroupKind.Pair],
            groups.Select(g => g.Kind));
    }

    [Fact]
    public void Every_tile_handed_in_comes_back_exactly_once_from_a_reading()
    {
        const string hand = "1344999b 22334678c 88d";

        var given = TileNotation.ParseRefs(hand).Select(t => t.Id).Order();
        var back = FromWin(hand, Tile.Parse("D8")).SelectMany(g => g.Tiles).Select(t => t.Id).Order();

        Assert.Equal(given, back);
    }

    [Fact]
    public void A_reading_that_does_not_fit_the_tiles_comes_back_empty_so_the_caller_can_fall_back()
    {
        // A reading of a different hand entirely. Laying it out would drop tiles off the screen,
        // so nothing is returned and the replay falls back to Arrange.
        var reading = new Decomposition([
            new HandSet(SetKind.Pung, Suit.Dots, 1, JokersUsed: 0),
            new HandSet(SetKind.Pair, Suit.Dots, 2, JokersUsed: 0),
        ]);

        Assert.Empty(HandArranger.FromReading(reading, TileNotation.ParseRefs("789b"), [], null));
    }
}
