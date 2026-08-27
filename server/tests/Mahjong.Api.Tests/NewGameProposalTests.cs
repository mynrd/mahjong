using Mahjong.Api;

namespace Mahjong.Api.Tests;

/// <summary>
/// The rule that a finished table does not deal again until everybody says so.
///
/// The arithmetic is all here, away from the database and the hub, because the interesting cases
/// are the awkward ones: a seat that answers, a seat that leaves, a seat that nobody is sitting in.
/// Each of those is a reason a table has not dealt, and they must not be able to cancel each other
/// out into a deal nobody agreed to.
/// </summary>
public class NewGameProposalTests
{
    private static readonly HashSet<int> AllFour = [0, 1, 2, 3];

    [Fact]
    public void Calling_a_game_is_agreeing_to_it()
    {
        var proposal = NewGameProposal.Open(bySeat: 0, alsoAccepted: []);

        Assert.Equal(0, proposal.ProposedBySeat);
        Assert.True(proposal.HasAccepted(0));
    }

    [Fact]
    public void Bots_are_in_from_the_start()
    {
        // A bot has no screen to be asked on. A table that waited for one would never deal again.
        var proposal = NewGameProposal.Open(bySeat: 0, alsoAccepted: [2, 3]);

        Assert.True(proposal.HasAccepted(2));
        Assert.True(proposal.HasAccepted(3));
        Assert.Equal([1], proposal.WaitingOn(AllFour));
    }

    [Fact]
    public void The_table_deals_only_once_every_seat_has_said_yes()
    {
        var proposal = NewGameProposal.Open(bySeat: 0, alsoAccepted: [3]);

        Assert.False(proposal.IsAgreedBy(AllFour));

        proposal = proposal.With(1);
        Assert.False(proposal.IsAgreedBy(AllFour));

        proposal = proposal.With(2);
        Assert.True(proposal.IsAgreedBy(AllFour));
    }

    [Fact]
    public void An_empty_seat_holds_the_table_up_however_many_people_have_agreed()
    {
        // Everybody still sitting there has said yes, and it still does not deal: an empty chair
        // is not a yes, and the host has to fill it before anything happens.
        var proposal = NewGameProposal
            .Open(bySeat: 0, alsoAccepted: [2, 3])
            .Without(1);

        var threeSeats = new HashSet<int> { 0, 2, 3 };

        Assert.True(threeSeats.All(proposal.HasAccepted));
        Assert.False(proposal.IsAgreedBy(threeSeats));
        Assert.Empty(proposal.WaitingOn(threeSeats));
    }

    [Fact]
    public void A_seat_that_is_filled_again_answers_for_itself()
    {
        // The player who said yes has gone. Whoever sits down next has not said anything, and the
        // seat's old answer must not be lying around waiting to be counted as theirs.
        var proposal = NewGameProposal
            .Open(bySeat: 0, alsoAccepted: [1, 2, 3])
            .Without(1);

        Assert.False(proposal.HasAccepted(1));
        Assert.False(proposal.IsAgreedBy(AllFour));
        Assert.Equal([1], proposal.WaitingOn(AllFour));

        Assert.True(proposal.With(1).IsAgreedBy(AllFour));
    }

    [Fact]
    public void Saying_yes_twice_is_saying_yes_once()
    {
        var proposal = NewGameProposal.Open(bySeat: 0, alsoAccepted: []).With(1).With(1);

        Assert.Equal([0, 1], proposal.Accepted.Order());
    }

    [Fact]
    public void An_answer_from_a_seat_nobody_is_sitting_in_cannot_make_a_table_deal()
    {
        // Seat 1 left after agreeing and the chair is still empty. Even if something managed to put
        // an answer back on that seat, three occupied seats are not four.
        var proposal = NewGameProposal.Open(bySeat: 0, alsoAccepted: [1, 2, 3]);

        Assert.False(proposal.IsAgreedBy(new HashSet<int> { 0, 2, 3 }));
    }
}
