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
    private static GameState Window(
        string botHand,
        string discard,
        Tile? joker = null,
        bool assisted = true)
    {
        var refs = TileNotation.ParseRefs($"{botHand} {discard}");
        var tile = refs[^1];

        var state = new GameState
        {
            Rules = RuleOptions.Default with { JokerEnabled = joker is not null, AssistEnabled = assisted },
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
            OpenedUtc = Now,
            DeadlineUtc = null,
        };

        return state;
    }

    /// <summary>A window the bot has already passed on, so only patience is left.</summary>
    private static GameState PassedOn(string botHand, string discard, bool assisted = false)
    {
        var state = Window(botHand, discard, assisted: assisted);
        state.Pending!.Passed.Add(BotSeat);
        return state;
    }

    private static int Patience => RuleOptions.Default.BotPatienceSeconds;

    [Fact]
    public void A_discard_it_holds_two_copies_of_is_punged()
    {
        // The replay case: R1 discarded, bot holding two, and it used to just wave it through.
        var state = Window("11r 123d 456b 99c 258d 3b", "1r");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat, Now));
        Assert.Equal(ClaimKind.Pung, move.Kind);

        // The tiles are named rather than left to the engine, because at an assist-off table an
        // unnamed claim is only half an answer and would sit on the tile until the seat finished.
        Assert.Equal(
            state.Hands[BotSeat].Concealed.Where(t => t.Tile.Code == "R1").Select(t => t.Id).Order(),
            move.TileIds.Order());
    }

    [Fact]
    public void Three_copies_take_the_kang_over_the_pung()
    {
        var state = Window("111r 123d 456b 99c 258d", "1r");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat, Now));
        Assert.Equal(ClaimKind.Kang, move.Kind);
    }

    [Fact]
    public void A_discard_that_completes_the_hand_is_taken_as_todas()
    {
        // Two copies of R1 in hand, so pung is on offer too. Todas has to outrank it.
        var state = Window("123d 456d 789d 456b 99c 11r", "1r");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat, Now));
        Assert.Equal(ClaimKind.Todas, move.Kind);
    }

    [Fact]
    public void A_tile_it_cannot_use_is_passed()
    {
        var state = Window("123d 456b 99c 258d 13c 7b", "1r");

        Assert.IsType<GameMove.Pass>(SimpleBot.Decide(state, BotSeat, Now));
    }

    [Fact]
    public void The_joker_face_is_never_punged()
    {
        // Two jokers would make a legal pung of R1, but they are worth more as wilds.
        var state = Window("11r 123d 456b 99c 258d 3b", "1r", joker: Tile.Parse("R1"));

        Assert.IsType<GameMove.Pass>(SimpleBot.Decide(state, BotSeat, Now));
    }

    [Fact]
    public void A_chow_that_does_not_cost_a_pair_is_taken()
    {
        // Seat 2 sits immediately after seat 1, so the chow-from-the-left rule allows it.
        var state = Window("45b 19d 19c 234w 13r 7d", "3b");

        var move = Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat, Now));
        Assert.Equal(ClaimKind.Chow, move.Kind);
    }

    [Fact]
    public void A_chow_that_would_break_a_pair_is_passed()
    {
        // The only run for B3 is B4-B5, and both are held twice.
        var state = Window("4455b 19d 19c 234w 1r", "3b");

        Assert.IsType<GameMove.Pass>(SimpleBot.Decide(state, BotSeat, Now));
    }

    [Fact]
    public void A_seat_that_already_answered_is_left_alone()
    {
        var state = Window("11r 123d 456b 99c 258d 3b", "1r");
        state.Pending!.Passed.Add(BotSeat);

        Assert.Null(SimpleBot.Decide(state, BotSeat, Now));
    }

    // ---------------------------------------------------------------- ending an unassisted window
    //
    // An unassisted claim window has no deadline: the only thing that ends one is the next seat
    // drawing. That is fine between people, and fatal with a bot in that seat - a bot that has
    // already passed has nothing else it will ever do, so one human who stops answering would
    // freeze the table for good. It waits, then plays on.

    [Fact]
    public void It_draws_through_an_unassisted_window_it_has_waited_out()
    {
        var state = PassedOn("123d 456b 99c 258d 3b 7b", "1r");

        Assert.IsType<GameMove.Draw>(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience)));
    }

    [Fact]
    public void It_waits_its_full_patience_first()
    {
        var state = PassedOn("123d 456b 99c 258d 3b 7b", "1r");

        Assert.Null(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience - 1)));
    }

    [Fact]
    public void It_does_not_draw_through_somebody_still_naming_their_tiles()
    {
        var state = PassedOn("123d 456b 99c 258d 3b 7b", "1r");

        // Seat 3 has pressed pung and is counting its own tiles against the discard. Nothing bounds
        // that now, and taking the tile out from under it is what the whole window is arranged not
        // to do - so the bot waits however long it takes.
        state.Pending!.Declared[3] = new DeclaredClaim(ClaimKind.Pung, [], AwaitingTiles: true);

        Assert.Null(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience)));
        Assert.Null(SimpleBot.Decide(state, BotSeat, Now.AddHours(1)));
    }

    [Fact]
    public void It_does_not_draw_through_a_call_that_is_already_finished()
    {
        var state = PassedOn("123d 456b 99c 258d 3b 7b", "1r");

        // That one is on its own beat and takes the tile when it runs out. Drawing would bin a call
        // the seat has already paid for.
        state.Pending!.Declared[3] = new DeclaredClaim(ClaimKind.Pung, []);
        state.Pending.DeadlineUtc = Now.AddSeconds(6);

        Assert.Null(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience)));
    }

    [Fact]
    public void It_draws_through_an_assisted_window_as_well()
    {
        // An assisted window used to close on its own six seconds, so the bot never had to end one.
        // Nothing closes on a clock now, so this draw is what paces every table with a bot in it.
        var state = PassedOn("123d 456b 99c 258d 3b 7b", "1r", assisted: true);

        Assert.Null(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience - 1)));
        Assert.IsType<GameMove.Draw>(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience)));
    }

    [Fact]
    public void Only_the_seat_due_to_play_next_ends_the_window()
    {
        var state = PassedOn("123d 456b 99c 258d 3b 7b", "1r");
        state.Pending!.Passed.Add(3);

        // Seat 3 sits two along from the discarder, so it is not the seat about to draw.
        Assert.Null(SimpleBot.Decide(state, 3, Now.AddSeconds(Patience)));
    }

    [Fact]
    public void It_answers_the_window_before_it_thinks_about_patience()
    {
        // Still holding a pung it wants, so the claim comes first however long it has been sitting.
        var state = Window("11r 123d 456b 99c 258d 3b", "1r", assisted: false);

        Assert.IsType<GameMove.Claim>(SimpleBot.Decide(state, BotSeat, Now.AddSeconds(Patience)));
    }
}
