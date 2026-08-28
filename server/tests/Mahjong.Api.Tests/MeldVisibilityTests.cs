using Mahjong.Domain;
using Mahjong.Domain.Tests;

namespace Mahjong.Api.Tests;

/// <summary>
/// Which four-tile groups the table draws face down, and which it draws face up.
///
/// Three different things all end up as four identical tiles sitting in front of a seat, and only
/// one of them is a secret: the kang a player built entirely out of their own draws. A kang
/// completed off somebody's discard, and a pung grown into a kang by sagasa, were both public
/// before they were four tiles, so there is nothing left to hide about either.
///
/// The client picks which way to draw a group off one field - <see cref="MeldView.Concealed"/> -
/// so if that field is wrong the table either shows a hand that was meant to stay hidden or hides
/// one everybody already saw. These check it for every way a kang can form, from every seat that
/// can look at it, because the owner and the other three read the same field.
/// </summary>
public class MeldVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The face every scenario below kangs on.</summary>
    private const string Face = "C5";

    private static readonly Dictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> Seats = new()
    {
        [0] = ("Mynard", false, true, 0),
        [1] = ("Tito Ben", false, true, 0),
        [2] = ("Ate Rose", false, true, 0),
        [3] = ("Kuya Jun", true, true, 0),
    };

    private static readonly int[] EverySeat = [0, 1, 2, 3];

    /// <summary>The one group in front of <paramref name="owner"/>, as <paramref name="viewer"/> gets it.</summary>
    private static MeldView MeldSeenBy(GameState state, int owner, int viewer) =>
        Assert.Single(GameViewBuilder.Build(state, "ABC123", viewer, Seats).Seats[owner].Melds);

    /// <summary>Seat 0 holds all four copies itself and declares.</summary>
    private static GameState SecretKang()
    {
        var table = TestTable.Build(t => t.Hand(0, "5555c").Filler(0, 13, Face));
        MahjongGame.DeclareSecretKang(table.State, 0, Tile.Parse(Face));
        return table.State;
    }

    /// <summary>Seat 3 holds three, seat 0 throws the fourth, seat 3 takes it.</summary>
    private static GameState OpenKang()
    {
        var table = TestTable.Build(t => t
            .Hand(3, "555c").Filler(3, 13, Face)
            .Filler(1, 16, Face)
            .Filler(2, 16, Face)
            .Hand(0, "5c").Filler(0, 16, Face));

        MahjongGame.Discard(table.State, 0, table.HeldId(0, Face), Now);
        MahjongGame.Claim(table.State, 3, ClaimKind.Kang, [], Now);
        MahjongGame.Pass(table.State, 1, Now);
        MahjongGame.Pass(table.State, 2, Now);

        return table.State;
    }

    /// <summary>Seat 0 punged earlier, then drew the fourth copy itself.</summary>
    private static GameState Sagasa()
    {
        var table = TestTable.Build(t => t
            .Meld(0, SetKind.Pung, "555c")
            .Hand(0, "5c").Filler(0, 13, Face));

        MahjongGame.DeclareSagasa(table.State, 0, Tile.Parse(Face));
        return table.State;
    }

    [Fact]
    public void A_secret_kang_is_marked_concealed_for_the_three_seats_that_did_not_declare_it()
    {
        var state = SecretKang();

        foreach (var viewer in EverySeat)
        {
            var meld = MeldSeenBy(state, owner: 0, viewer);

            Assert.Equal(SetKind.Kang, meld.Kind);
            Assert.Equal(4, meld.Tiles.Count);

            // The owner reads the same flag as everybody else. It says what kind of group this is,
            // not who is allowed to see it: the client draws backs for the three seats that are not
            // holding it and the real faces for the one that is.
            Assert.True(meld.Concealed);

            // Nobody's discard went into it, which is the whole reason it is a secret.
            Assert.Null(meld.ClaimedFromSeat);
            Assert.False(meld.FromSagasa);
        }
    }

    [Fact]
    public void A_kang_completed_off_a_discard_is_face_up_to_everybody()
    {
        var state = OpenKang();

        foreach (var viewer in EverySeat)
        {
            var meld = MeldSeenBy(state, owner: 3, viewer);

            Assert.Equal(SetKind.Kang, meld.Kind);
            Assert.Equal(4, meld.Tiles.Count);
            Assert.False(meld.Concealed);
            Assert.Equal(0, meld.ClaimedFromSeat);
        }
    }

    [Fact]
    public void A_sagasa_kang_is_face_up_because_the_pung_underneath_it_already_was()
    {
        var state = Sagasa();

        foreach (var viewer in EverySeat)
        {
            var meld = MeldSeenBy(state, owner: 0, viewer);

            Assert.Equal(SetKind.Kang, meld.Kind);
            Assert.Equal(4, meld.Tiles.Count);
            Assert.True(meld.FromSagasa);

            // The fourth tile was drawn, not claimed - but the three under it were on the table
            // already, so hiding the group now would hide something the table has been looking at.
            Assert.False(meld.Concealed);
        }
    }

    [Fact]
    public void The_secret_kang_is_the_only_one_of_the_three_that_is_hidden()
    {
        Assert.True(MeldSeenBy(SecretKang(), owner: 0, viewer: 1).Concealed);
        Assert.False(MeldSeenBy(OpenKang(), owner: 3, viewer: 1).Concealed);
        Assert.False(MeldSeenBy(Sagasa(), owner: 0, viewer: 1).Concealed);
    }

    [Fact]
    public void A_secret_kang_is_still_hidden_once_the_hand_is_over_and_the_owner_shows_their_hand()
    {
        var state = SecretKang();
        state.Phase = GamePhase.HandOver;

        // Turning your hand face up at the end shows the tiles you were still holding. The kang is
        // not one of them: it left the hand when it was declared, and it stays a concealed group.
        var view = GameViewBuilder.Build(state, "ABC123", forSeat: 1, Seats, revealed: new HashSet<int> { 0 });

        Assert.NotNull(view.Seats[0].Concealed);
        Assert.True(Assert.Single(view.Seats[0].Melds).Concealed);
    }

    [Fact]
    public void The_faces_of_a_secret_kang_are_sent_to_the_other_seats_and_only_hidden_by_the_client()
    {
        // Not the behaviour anyone would design, but it is the behaviour, so it is written down.
        // A seat that reads the wire instead of the screen can name a secret kang the moment it is
        // declared. Closing it means withholding the tiles in GameViewBuilder.ToView the way a
        // concealed hand is withheld, and this test is what will fail when that is done.
        var meld = MeldSeenBy(SecretKang(), owner: 0, viewer: 1);

        Assert.All(meld.Tiles, tile => Assert.Equal(Face, tile.Code));
    }
}
