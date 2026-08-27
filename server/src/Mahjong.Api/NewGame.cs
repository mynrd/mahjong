namespace Mahjong.Api;

/// <summary>
/// A standing offer of another game, and who has taken it up.
///
/// The seat that made the table calls the next game, but calling it does not deal it: a table only
/// deals when all four seats have said yes, and the caller waits with everybody else. That is the
/// whole reason this type exists rather than the host simply pressing Start again - four people who
/// have just finished a hand are not necessarily four people who want another one, and the old
/// behaviour dealt over the top of that question without ever asking it.
///
/// Immutable, and replaced whole on every change, so the code that reads it while building a view
/// never sees a half-written one. Bots are accepted the moment they are seated: a bot has no screen
/// to be asked on, and a table that waited for one would never start.
/// </summary>
/// <param name="ProposedBySeat">The seat that called it, which is always the host's.</param>
/// <param name="Accepted">Seats that have said yes, including the caller's own.</param>
public sealed record NewGameProposal(int ProposedBySeat, IReadOnlySet<int> Accepted)
{
    /// <summary>Opens a proposal. The caller is in from the start: calling it is agreeing to it.</summary>
    public static NewGameProposal Open(int bySeat, IEnumerable<int> alsoAccepted) =>
        new(bySeat, new HashSet<int>(alsoAccepted) { bySeat });

    public NewGameProposal With(int seat) => this with { Accepted = new HashSet<int>(Accepted) { seat } };

    /// <summary>
    /// Takes a seat's answer back off the proposal, for when the player in it leaves or is removed.
    /// The seat is empty now, and the next person to sit in it answers for themselves.
    /// </summary>
    public NewGameProposal Without(int seat) =>
        this with { Accepted = new HashSet<int>(Accepted.Where(s => s != seat)) };

    public bool HasAccepted(int seat) => Accepted.Contains(seat);

    /// <summary>
    /// Whether the table can deal. Every seat has to be taken and every seat has to have said yes:
    /// an empty seat is not a yes, which is why a player who leaves rather than agreeing holds the
    /// next game up until the host fills the seat they left.
    /// </summary>
    public bool IsAgreedBy(IReadOnlySet<int> occupiedSeats) =>
        occupiedSeats.Count == MahjongSeats && occupiedSeats.All(Accepted.Contains);

    /// <summary>Seats that are taken but have not answered yet. What the table is waiting on.</summary>
    public IReadOnlyList<int> WaitingOn(IReadOnlySet<int> occupiedSeats) =>
        occupiedSeats.Where(s => !Accepted.Contains(s)).Order().ToArray();

    private const int MahjongSeats = 4;
}
