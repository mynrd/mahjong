using Mahjong.Api;
using Mahjong.Domain;

namespace Mahjong.Api.Tests;

/// <summary>
/// KANG-RULE.md: a seat that is not the mano is dealt four of a face, takes its turn, and wants to
/// lay the kang down. Nothing covered the offer itself before this. The domain test calls
/// <see cref="MahjongGame.DeclareSecretKang"/> straight, so the view builder was never asked
/// whether it would have drawn the button - and the button is the only way a player reaches the
/// move.
/// </summary>
public class SecretKangOfferTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private const string Kang = "B8";

    private static readonly Dictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> Seats = new()
    {
        [0] = ("Mynard", false, true, 0),
        [1] = ("Tito Ben", false, true, 0),
        [2] = ("Ate Rose", false, true, 0),
        [3] = ("Kuya Jun", false, true, 0),
    };

    /// <summary>
    /// Mano is seat 0 throughout. The seat named holds four 8 bamboo out of the deal plus twelve
    /// tiles that form nothing, so the only kang in the hand is the one under test.
    /// </summary>
    private static GameState DealtFourBamboo(
        int seat,
        GamePhase phase,
        int currentSeat,
        Tile? joker = null,
        bool assist = true)
    {
        var hand = TileNotation.ParseRefs("8888b 123456789d 123c");

        var state = new GameState
        {
            Rules = RuleOptions.Default with { JokerEnabled = joker is not null, AssistEnabled = assist },
            HandNumber = 1,
            ManoSeat = 0,
            Seed = 1,
            Wall = TileSet.All.Where(t => hand.All(r => r.Id != t.Id)).ToList(),
            FrontIndex = 0,
            CurrentSeat = currentSeat,
            Phase = phase,
            Joker = joker,
        };

        state.BackIndex = state.Wall.Count - 1;
        state.Hands[seat].Concealed.AddRange(hand);

        return state;
    }

    private static TurnOptionsView? TurnFor(GameState state, int seat) =>
        GameViewBuilder.Build(state, "ABC123", seat, Seats).YourTurn;

    [Fact]
    public void A_non_mano_dealt_four_of_a_face_is_offered_it_after_drawing()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDraw, currentSeat: 2);

        MahjongGame.Draw(state, 2, Now);

        var turn = TurnFor(state, 2);

        Assert.NotNull(turn);
        Assert.Equal(GamePhase.AwaitingDiscard, state.Phase);
        Assert.Contains(Kang, turn.SecretKangFaces);
    }

    /// <summary>Every seat, not just the one the bug report happened to be sitting in.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Any_seat_is_offered_it_on_its_own_turn(int seat)
    {
        var state = DealtFourBamboo(seat, GamePhase.AwaitingDraw, currentSeat: seat);

        MahjongGame.Draw(state, seat, Now);

        var turn = TurnFor(state, seat);

        Assert.NotNull(turn);
        Assert.Contains(Kang, turn.SecretKangFaces);
    }

    /// <summary>
    /// Real tables run with the joker on; leaving it off would have made this whole file agree
    /// with a game nobody plays. Four of a face is still four of a face when another face is wild,
    /// and still four when the wild face is this one.
    /// </summary>
    [Theory]
    [InlineData("D3")]
    [InlineData(Kang)]
    public void The_joker_does_not_take_the_offer_away(string jokerCode)
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDraw, currentSeat: 2, joker: Tile.Parse(jokerCode));

        MahjongGame.Draw(state, 2, Now);

        var turn = TurnFor(state, 2);

        Assert.NotNull(turn);
        Assert.Contains(Kang, turn.SecretKangFaces);
    }

    /// <summary>
    /// Assist off hides what other seats could build with a discard. Your own four tiles are not
    /// a hint about anybody else's hand, so they stay offered.
    /// </summary>
    [Fact]
    public void Assist_off_still_offers_it()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDraw, currentSeat: 2, assist: false);

        MahjongGame.Draw(state, 2, Now);

        var turn = TurnFor(state, 2);

        Assert.NotNull(turn);
        Assert.Contains(Kang, turn.SecretKangFaces);
    }

    /// <summary>
    /// A seat that took a discard is in AwaitingDiscard without having drawn. A kang it was dealt
    /// is still four tiles in its hand, so the offer has to survive that route in as well.
    /// </summary>
    [Fact]
    public void It_is_offered_on_the_turn_a_claim_started()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDiscard, currentSeat: 2);

        var turn = TurnFor(state, 2);

        Assert.NotNull(turn);
        Assert.Contains(Kang, turn.SecretKangFaces);
    }

    /// <summary>The mano, who is in AwaitingDiscard from the deal with seventeen tiles.</summary>
    [Fact]
    public void The_mano_is_offered_it_straight_off_the_deal()
    {
        var state = DealtFourBamboo(seat: 0, GamePhase.AwaitingDiscard, currentSeat: 0);
        state.Hands[0].Concealed.Add(state.Wall[state.FrontIndex++]);

        var turn = TurnFor(state, 0);

        Assert.NotNull(turn);
        Assert.Contains(Kang, turn.SecretKangFaces);
    }

    [Fact]
    public void The_offer_is_not_there_before_that_seat_draws()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDraw, currentSeat: 2);

        Assert.Null(TurnFor(state, 2));
    }

    /// <summary>Three copies is not a kang, and the button must not appear over one.</summary>
    [Fact]
    public void Three_of_a_face_is_not_offered()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDiscard, currentSeat: 2);
        var hand = state.Hands[2].Concealed;
        hand.Remove(hand.First(t => t.Tile == Tile.Parse(Kang)));

        var turn = TurnFor(state, 2);

        Assert.NotNull(turn);
        Assert.DoesNotContain(Kang, turn.SecretKangFaces);
    }

    /// <summary>
    /// The two halves have to agree. A face the view offers that the domain then rejects is the
    /// same dead button to the player as no button at all.
    /// </summary>
    [Fact]
    public void The_face_the_view_offers_is_one_the_domain_accepts()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDraw, currentSeat: 2);

        MahjongGame.Draw(state, 2, Now);

        var face = Assert.Single(TurnFor(state, 2)!.SecretKangFaces);
        MahjongGame.DeclareSecretKang(state, 2, Tile.Parse(face));

        var meld = Assert.Single(state.Hands[2].Melds);
        Assert.Equal(SetKind.Kang, meld.Kind);
        Assert.True(meld.Concealed);
        Assert.Equal(17, state.Hands[2].TileCount);
    }

    /// <summary>
    /// After it is down: the seat is still on its own turn and still owes the table a discard, and
    /// the offer is gone because the tiles have left the hand.
    /// </summary>
    [Fact]
    public void Declaring_it_leaves_the_seat_on_its_own_turn_still_owing_a_discard()
    {
        var state = DealtFourBamboo(seat: 2, GamePhase.AwaitingDraw, currentSeat: 2);

        MahjongGame.Draw(state, 2, Now);
        MahjongGame.DeclareSecretKang(state, 2, Tile.Parse(Kang));

        Assert.Equal(GamePhase.AwaitingDiscard, state.Phase);
        Assert.Equal(2, state.CurrentSeat);

        var turn = TurnFor(state, 2);
        Assert.NotNull(turn);
        Assert.True(turn.CanDiscard);
        Assert.DoesNotContain(Kang, turn.SecretKangFaces);
    }
}
