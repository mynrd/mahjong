using Mahjong.Domain;

namespace Mahjong.Api.Tests;

/// <summary>
/// The bot fills empty seats at a real table, so a discard it could take and does not is a seat
/// the humans get to play against for free. These pin the claim policy down: what it takes, in
/// what order, and the two trades it refuses.
/// </summary>
public class SimpleBotTests
{
    private const int BotSeat = 2;
    private const int DiscarderSeat = 1;

    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a claim window on <paramref name="discard"/> with the bot on seat 2 holding
    /// <paramref name="botHand"/>. Hand and discard are cut from one id pool, so the discarded
    /// tile is never a copy the bot is also holding.
    /// </summary>
    private static GameState Window(string botHand, string discard, Tile? joker = null)
    {
        var refs = TileNotation.ParseRefs($"{botHand} {discard}");
        var tile = refs[^1];

        var state = new GameState
        {
            Rules = RuleOptions.Default with { JokerEnabled = joker is not null },
            HandNumber = 1,
            ManoSeat = 0,
            Seed = 1,
            Wall = TileSet.All.Where(t => refs.All(r => r.Id != t.Id)).ToList(),
            FrontIndex = 0,
            Joker = joker,
            CurrentSeat = DiscarderSeat,
            Phase = GamePhase.AwaitingClaims,
        };

        state.BackIndex = state.Wall.Count - 1;
        state.Hands[BotSeat].Concealed.AddRange(refs.Take(refs.Count - 1));
        state.Discards.Add(new DiscardedTile(DiscarderSeat, tile));
        state.Pending = new PendingClaim
        {
            Tile = tile,
            FromSeat = DiscarderSeat,
            DeadlineUtc = Now.AddSeconds(6),
        };

        return state;
    }

    [Fact]
    public void A_discard_it_holds_two_copies_of_is_punged()
    {
        // The replay case: R1 discarded, bot holding two, and it used to just wave it through.
        var state = Window("11r 123d 456b 99c 258d 3b", "1r");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat));
        Assert.Equal(ClaimKind.Pung, move.Kind);
        Assert.Empty(move.TileIds);
    }

    [Fact]
    public void Three_copies_take_the_kang_over_the_pung()
    {
        var state = Window("111r 123d 456b 99c 258d", "1r");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat));
        Assert.Equal(ClaimKind.Kang, move.Kind);
    }

    [Fact]
    public void A_discard_that_completes_the_hand_is_taken_as_todas()
    {
        // Two copies of R1 in hand, so pung is on offer too. Todas has to outrank it.
        var state = Window("123d 456d 789d 456b 99c 11r", "1r");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat));
        Assert.Equal(ClaimKind.Todas, move.Kind);
    }

    [Fact]
    public void A_tile_it_cannot_use_is_passed()
    {
        var state = Window("123d 456b 99c 258d 13c 7b", "1r");

        Assert.IsType<GameMove.Pass>(SimpleBot.Decide(state, BotSeat));
    }

    [Fact]
    public void The_joker_face_is_never_punged()
    {
        // Two jokers would make a legal pung of R1, but they are worth more as wilds.
        var state = Window("11r 123d 456b 99c 258d 3b", "1r", joker: Tile.Parse("R1"));

        Assert.IsType<GameMove.Pass>(SimpleBot.Decide(state, BotSeat));
    }

    [Fact]
    public void A_chow_that_does_not_cost_a_pair_is_taken()
    {
        // Seat 2 sits immediately after seat 1, so the chow-from-the-left rule allows it.
        var state = Window("45b 19d 19c 234w 13r 7d", "3b");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat));
        Assert.Equal(ClaimKind.Chow, move.Kind);
    }

    [Fact]
    public void A_chow_that_would_break_a_pair_is_passed()
    {
        // The only run for B3 is B4-B5, and both are held twice.
        var state = Window("4455b 19d 19c 234w 1r", "3b");

        Assert.IsType<GameMove.Pass>(SimpleBot.Decide(state, BotSeat));
    }

    [Fact]
    public void A_seat_that_already_answered_is_left_alone()
    {
        var state = Window("11r 123d 456b 99c 258d 3b", "1r");
        state.Pending!.Passed.Add(BotSeat);

        Assert.Null(SimpleBot.Decide(state, BotSeat));
    }
}
