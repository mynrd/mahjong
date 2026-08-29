using Mahjong.Api;
using Mahjong.Domain;

namespace Mahjong.Api.Tests;

/// <summary>
/// <see cref="TurnOptionsView.CanDeclareTodas"/> is the only thing the table checks before drawing
/// the Todas button on your own turn. A pung that finishes the hand leaves 17 tiles with nothing
/// drawn, and that flag used to be false there - so the seat holding a won hand was shown Discard
/// and nothing else.
/// </summary>
public class TurnOptionsViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> Seats = new()
    {
        [0] = ("Mynard", false, true, 0),
        [1] = ("Tito Ben", false, true, 0),
        [2] = ("Ate Rose", false, true, 0),
        [3] = ("Kuya Jun", false, true, 0),
    };

    /// <summary>
    /// Seat 3 has four groups down and 5c 5c plus <paramref name="tail"/> in hand, and seat 0
    /// throws the third 5 chars. Seat 3 pungs it, which is where the bug was: with 9b 9b left the
    /// hand is complete and the table still only offered Discard.
    /// </summary>
    private static GameState AfterPunging(string tail)
    {
        var thrown = TileNotation.ParseRefs("5c");
        var hand = TileNotation.ParseRefs($"55c {tail}");
        var melds = TileNotation.ParseRefs("123d 456d 789d 234b");
        var held = thrown.Concat(hand).Concat(melds).ToList();

        var state = new GameState
        {
            Rules = RuleOptions.Default with { JokerEnabled = false },
            HandNumber = 1,
            ManoSeat = 0,
            Seed = 1,
            Wall = TileSet.All.Where(t => held.All(r => r.Id != t.Id)).ToList(),
            FrontIndex = 0,
            CurrentSeat = 0,
            Phase = GamePhase.AwaitingDiscard,
        };

        state.BackIndex = state.Wall.Count - 1;
        state.Hands[0].Concealed.AddRange(thrown);
        state.Hands[3].Concealed.AddRange(hand);

        for (var i = 0; i < 4; i++)
            state.Hands[3].Melds.Add(new ExposedMeld(SetKind.Chow, melds.Skip(i * 3).Take(3).ToList(), Concealed: false, ClaimedFromSeat: 1));

        MahjongGame.Discard(state, 0, thrown[0].Id, Now);
        MahjongGame.Claim(state, 3, ClaimKind.Pung, [], Now);
        MahjongGame.Pass(state, 1, Now);
        MahjongGame.Pass(state, 2, Now);

        return state;
    }

    private static TurnOptionsView? TurnFor(GameState state, int seat) =>
        GameViewBuilder.Build(state, "ABC123", seat, Seats).YourTurn;

    [Fact]
    public void A_pair_left_after_a_pung_is_offered_as_todas()
    {
        var turn = TurnFor(AfterPunging("99b"), 3);

        Assert.NotNull(turn);
        Assert.True(turn.CanDeclareTodas);
    }

    [Fact]
    public void Two_odd_tiles_left_after_a_pung_are_not()
    {
        var turn = TurnFor(AfterPunging("89b"), 3);

        Assert.NotNull(turn);
        Assert.False(turn.CanDeclareTodas);
    }

    [Fact]
    public void Discard_stays_open_alongside_the_offer()
    {
        // Todas is offered, not forced: the seat can still throw a tile if that is what it wants.
        var turn = TurnFor(AfterPunging("99b"), 3);

        Assert.NotNull(turn);
        Assert.True(turn.CanDiscard);
    }
}
