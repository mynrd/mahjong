using System.Text.Json.Serialization;
using Mahjong.Domain;

namespace Mahjong.Api;

/// <summary>A tile as the client sees it: a stable id plus the face code, e.g. 42 and "D5".</summary>
public sealed record TileView(int Id, string Code)
{
    public static TileView Of(TileRef tile) => new(tile.Id, tile.Tile.Code);
}

public sealed record MeldView(
    SetKind Kind,
    IReadOnlyList<TileView> Tiles,
    bool Concealed,
    int? ClaimedFromSeat,
    bool FromSagasa);

/// <summary>One block of your own hand, as Auto Arrange would lay it out.</summary>
/// <param name="Needs">Face codes that would complete this group, e.g. ["B2","B5"].</param>
public sealed record HandGroupView(
    HandGroupKind Kind,
    IReadOnlyList<TileView> Tiles,
    IReadOnlyList<string> Needs,
    int JokersUsed);

/// <summary>
/// One seat as seen by one particular player. <see cref="Concealed"/> is filled in only for the
/// seat the view belongs to; for everyone else the client gets <see cref="ConcealedCount"/> and
/// nothing more. This split is the whole point of the type.
/// </summary>
/// <param name="Groups">
/// The concealed tiles read as blocks, for Auto Arrange. Derived from <see cref="Concealed"/>, so
/// it is filled in for the viewer's own seat and null for everyone else, for the same reason.
/// </param>
/// <param name="Revealed">
/// This seat has turned its hand face up now that the hand is over, so <see cref="Concealed"/> is
/// filled in for everybody rather than for its owner alone. Only ever true once the hand is over.
/// </param>
public sealed record SeatStateView(
    int Seat,
    /// <summary>
    /// Null when nobody is sitting there, which is a thing that happens now: a player who says no
    /// to another game leaves, and the chair stays empty until it is filled.
    ///
    /// Written even when it is null, against the serialiser's default of leaving nulls out. An
    /// absent property and a null one are the same thing to most readers and not to a strict one,
    /// and "is this seat empty" is exactly the question a client asks of this field.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? DisplayName,
    bool IsBot,
    bool IsConnected,
    int ConcealedCount,
    IReadOnlyList<TileView>? Concealed,
    IReadOnlyList<HandGroupView>? Groups,
    IReadOnlyList<MeldView> Melds,
    IReadOnlyList<TileView> Bonus,
    int Balance,
    bool Revealed = false);

public sealed record DiscardView(int Seat, TileView Tile, bool Claimed);

/// <summary>
/// One concrete way this seat could take the discard, with the exact tiles it costs.
/// </summary>
/// <param name="TileIds">
/// Ids from this seat's own hand, not including the discard. Empty for a Todas, where the win is
/// read off the whole hand.
/// </param>
/// <param name="Describe">Label for the button, e.g. "Chow B3-B4-B5" or "Pung B5".</param>
public sealed record ClaimCandidateView(
    ClaimKind Kind,
    IReadOnlyList<int> TileIds,
    string Describe);

/// <summary>Offered to a player while somebody else's discard is still claimable.</summary>
/// <param name="YourOptions">The claim kinds available, kept for callers that only need the kinds.</param>
/// <param name="Candidates">
/// The expanded form of <paramref name="YourOptions"/>: one entry per distinct legal group, so a
/// tile claimable as either a chow or a pung produces two entries, and two possible chows produce
/// two entries. Highest-ranked kind first.
/// </param>
/// <param name="DeadlineUtc">
/// When the call standing on the tile takes it, or null while nobody has called on it - which is
/// the normal state of a window, because no seat is ever timed for answering a discard.
/// </param>
/// <param name="PressedKind">
/// Assist off: what this seat has pressed and is still naming tiles for. The dialog stays open on
/// it until the tiles are named or the call is let go. Nothing is counting against it.
/// </param>
/// <param name="YouClaimed">
/// This seat has a claim on the tile, finished or half made. Distinct from <paramref name="YouAnswered"/>,
/// which is also true of a plain pass: the client needs the difference to know whether offering to
/// draw through the window would be throwing away something the player already called.
/// </param>
/// <summary>Where one seat has got to on the open discard, as much of it as the table can hear.</summary>
public enum SeatCallState
{
    /// <summary>Has not answered yet. This is who the table is waiting on.</summary>
    Waiting,

    /// <summary>Said they do not want it.</summary>
    Passed,

    /// <summary>Has called, and is still choosing which of their tiles it costs.</summary>
    Calling,

    /// <summary>Has called and named the tiles. Nothing under this can take the discard now.</summary>
    Called,

    /// <summary>Called something a stronger call beat, and was answered for.</summary>
    Outranked,

}

/// <summary>
/// One seat's public part in the open claim window. The kind is sent whenever there is one, for the
/// same reason a call is shouted rather than whispered: the three seats that did not make it have
/// to know it happened before they spend the window answering a tile that is already gone.
/// </summary>
public sealed record SeatCallView(int Seat, SeatCallState State, ClaimKind? Called);

public sealed record ClaimPromptView(
    TileView Tile,
    int FromSeat,
    DateTimeOffset? DeadlineUtc,
    /// <summary>
    /// How long the beat above was when it started. The deadline alone says when the tile goes but
    /// not how much time there was, and a countdown bar needs both to know how full to draw itself.
    /// Sent rather than assumed, because the length is a house rule a table can set to anything.
    /// </summary>
    int WindowSeconds,
    IReadOnlyList<ClaimKind> YourOptions,
    IReadOnlyList<ClaimCandidateView> Candidates,
    bool YouAnswered,
    ClaimKind? PressedKind,
    /// <summary>
    /// What this seat has called on the tile, half made or finished, or null if it has not called.
    /// The bar says it back while the window is open, next to the way out of it: a call holds the
    /// tile against the other three, so the seat holding it has to be able to see that it does.
    /// </summary>
    ClaimKind? YourCall,
    bool YouClaimed,
    /// <summary>
    /// Your own call was beaten by a stronger one before the window closed, so you were answered
    /// for. Sent alongside <paramref name="YouAnswered"/> rather than folded into it, because
    /// "you passed" and "a pung took it off you" are the same fact to the engine and nothing
    /// like the same thing to the person who had been choosing tiles.
    /// </summary>
    bool Outranked,
    /// <summary>
    /// The calls still worth pressing. Everything at or under a call already made out loud is left
    /// out: rank arithmetic over what the whole table heard, never a reading of your hand.
    /// </summary>
    IReadOnlyList<ClaimKind> LiveKinds,
    /// <summary>
    /// What each other seat has said about this discard, in the order they sit. A call at a real
    /// table is shouted and everybody hears it at once; sending this is what stops the server
    /// holding one silently while somebody else builds a group that cannot beat it.
    /// </summary>
    IReadOnlyList<SeatCallView> Calls,
    /// <summary>
    /// Whether a chow off this tile is open to this seat by position and by the tile itself, which
    /// is public: everybody can see the tile and who threw it. What the hand holds does not come
    /// into it, so it is sent even at a table with assist off - and there it is the only thing that
    /// keeps the Chow button off a tile no seat in this chair could ever chow.
    /// </summary>
    bool ChowPossible);

/// <summary>What the player whose turn it is may do right now.</summary>
public sealed record TurnOptionsView(
    bool CanDiscard,
    bool CanDeclareTodas,
    IReadOnlyList<string> SecretKangFaces,
    IReadOnlyList<string> SagasaFaces);

public sealed record ScoreLineView(string Name, int Units);

/// <summary>
/// The standing offer of another game, as the whole table sees it.
///
/// Sent to everybody, not just to the seats still deciding: the point of asking rather than dealing
/// is that people can see who is holding the table up, and a list only the host could read would be
/// the same silence with an extra step.
/// </summary>
/// <param name="Accepted">Seats that have said yes. A seat nobody is sitting in is never in here.</param>
public sealed record NewGameView(int ProposedBySeat, IReadOnlyList<int> Accepted);

public sealed record OutcomeView(
    HandEndReason Reason,
    int? WinnerSeat,
    int TotalUnits,
    IReadOnlyList<ScoreLineView> Breakdown,
    IReadOnlyList<Settlement> Settlements);

/// <summary>
/// The only shape of game state that is ever sent to a client. Built from <see cref="GameState"/>
/// for one specific seat.
///
/// Two things are deliberately absent and must stay absent: the wall (its order would give away
/// every future draw) and other seats' concealed tiles. Both live on <see cref="GameState"/>,
/// which is why that type never leaves the server.
/// </summary>
public sealed record PlayerGameView(
    string RoomCode,
    int HandNumber,
    int YourSeat,
    int ManoSeat,
    int CurrentSeat,
    GamePhase Phase,
    string? Joker,
    int TilesRemaining,
    IReadOnlyList<SeatStateView> Seats,
    IReadOnlyList<DiscardView> Discards,
    ClaimPromptView? Claim,
    TurnOptionsView? YourTurn,
    OutcomeView? Outcome,
    /// <summary>
    /// Which seat made the table. Sent so the client can draw the actions only that seat has -
    /// calling the next game, removing somebody who has stopped answering - from what the server
    /// says rather than from what the browser remembers about itself.
    /// </summary>
    int? HostSeat,
    /// <summary>The offer of another game, or null when nobody has called one.</summary>
    NewGameView? NewGame,
    /// <summary>
    /// Whether this table lets the server help. Off, no claim is ever spelled out and no hand is
    /// ever laid out for you. The client needs it on the view rather than only in the room's rules
    /// because it changes what the table draws, not just what it is allowed to send.
    /// </summary>
    bool Assisted)
{
    public bool IsYourTurn => CurrentSeat == YourSeat && Phase is GamePhase.AwaitingDraw or GamePhase.AwaitingDiscard;
}

public static class GameViewBuilder
{
    /// <summary>Builds the redacted view of <paramref name="state"/> for one seat.</summary>
    /// <param name="forSeat">
    /// The seat the view is for. Pass -1 for a view with every hand hidden, which is what a
    /// spectator or an audit dump would get.
    /// </param>
    /// <param name="revealed">
    /// Seats that have turned their hand face up, which only happens once the hand is over. Null
    /// for the ordinary case of nobody having shown anything.
    /// </param>
    public static PlayerGameView Build(
        GameState state,
        string roomCode,
        int forSeat,
        IReadOnlyDictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> seatInfo,
        IReadOnlySet<int>? revealed = null,
        int? hostSeat = null,
        NewGameProposal? proposal = null)
    {
        var seats = new List<SeatStateView>(MahjongGame.Seats);

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var hand = state.Hands[seat];
            var info = seatInfo.TryGetValue(seat, out var found) ? found : (Name: (string?)null, IsBot: false, IsConnected: false, Balance: 0);

            // Shown to the rest of the table only once the hand is finished, and only because the
            // player who holds them asked for it. The phase is checked here as well as where the
            // request is accepted: this is the line that decides what leaves the server, so it does
            // not take anybody's word for what the phase was.
            var shown = state.Phase == GamePhase.HandOver && revealed?.Contains(seat) == true;

            seats.Add(new SeatStateView(
                seat,
                info.Name,
                info.IsBot,
                info.IsConnected,
                hand.Concealed.Count,
                // The one line that decides whether this game is cheatable.
                seat == forSeat || shown ? hand.Concealed.Select(TileView.Of).ToList() : null,
                // Same rule: the grouping is read off the concealed tiles, so handing it to
                // anyone else would hand them the hand. And with assist off nobody gets it at all,
                // including its owner: reading your own hand is the thing the setting is about.
                seat == forSeat && state.Rules.AssistEnabled ? BuildGroups(state, hand) : null,
                hand.Melds.Select(ToView).ToList(),
                hand.Bonus.Select(TileView.Of).ToList(),
                info.Balance,
                shown));
        }

        return new PlayerGameView(
            roomCode,
            state.HandNumber,
            forSeat,
            state.ManoSeat,
            state.CurrentSeat,
            state.Phase,
            state.Joker?.Code,
            state.TilesRemaining,
            seats,
            state.Discards.Select(d => new DiscardView(d.Seat, TileView.Of(d.Tile), d.Claimed)).ToList(),
            BuildClaim(state, forSeat),
            BuildTurnOptions(state, forSeat),
            BuildOutcome(state),
            hostSeat,
            // Only the seats somebody is actually sitting in. An empty seat cannot have agreed to
            // anything, and listing it as undecided is what tells the table it needs filling.
            proposal is null
                ? null
                : new NewGameView(
                    proposal.ProposedBySeat,
                    proposal.Accepted.Where(seatInfo.ContainsKey).Order().ToList()),
            state.Rules.AssistEnabled);
    }

    private static IReadOnlyList<HandGroupView> BuildGroups(GameState state, PlayerHand hand) =>
        HandArranger.Arrange(hand.Concealed, hand.Melds, state.Joker, state.Rules)
            .Select(g => new HandGroupView(
                g.Kind,
                g.Tiles.Select(TileView.Of).ToList(),
                g.Needs.Select(t => t.Code).ToList(),
                g.JokersUsed))
            .ToList();

    private static MeldView ToView(ExposedMeld meld) => new(
        meld.Kind,
        // A secret kang is shown face down to everyone, including its owner's opponents. The tiles
        // are still sent because the owner is allowed to see their own, and the client renders the
        // back for other seats. Kind and count alone would not let the owner check their own hand.
        meld.Tiles.Select(TileView.Of).ToList(),
        meld.Concealed,
        meld.ClaimedFromSeat,
        meld.FromSagasa);

    private static ClaimPromptView? BuildClaim(GameState state, int forSeat)
    {
        if (state.Pending is not { } pending || forSeat < 0) return null;
        if (pending.FromSeat == forSeat) return null;

        var assisted = state.Rules.AssistEnabled;

        // Assist on, what this seat could build with the tile. Assist off, nothing: working that
        // out is the game. Either way the prompt itself goes to all three seats, so that a prompt
        // arriving never means "there is something here for you".
        var candidates = assisted
            ? MahjongGame.ClaimCandidates(state, pending.Tile, pending.FromSeat, forSeat)
            : [];

        pending.Declared.TryGetValue(forSeat, out var mine);

        // A seat part way through a press has not answered: the dialog has to stay up, because
        // naming the tiles is the half of the answer it still owes.
        var answered = pending.Passed.Contains(forSeat) || mine is { AwaitingTiles: false };

        return new ClaimPromptView(
            TileView.Of(pending.Tile),
            pending.FromSeat,
            pending.DeadlineUtc,
            state.Rules.ClaimWindowSeconds,
            candidates.Select(c => c.Kind).Distinct().ToList(),
            // Described server side, so the button text and the spoken label cannot drift from
            // what the candidate actually is.
            candidates
                .Select(c => new ClaimCandidateView(
                    c.Kind,
                    c.Support.Select(t => t.Id).ToList(),
                    c.Describe(pending.Tile)))
                .ToList(),
            answered,
            mine is { AwaitingTiles: true } ? mine.Kind : null,
            mine?.Kind,
            mine is not null,
            pending.Outranked.Contains(forSeat),
            MahjongGame.LiveKinds(state, pending, forSeat),
            BuildCalls(state, pending, forSeat),
            MahjongGame.ChowPossible(state, pending.Tile, pending.FromSeat, forSeat));
    }

    /// <summary>
    /// Where the other two answering seats have got to. The discarder is left out: they threw the
    /// tile, so they have no answer to give and are waiting on the same people this seat is.
    ///
    /// What a seat called is public here on purpose. At a table it is shouted, and the whole reason
    /// a chow could be built against a pung nobody had mentioned is that the server was holding
    /// that shout until the window closed.
    /// </summary>
    private static IReadOnlyList<SeatCallView> BuildCalls(
        GameState state, PendingClaim pending, int forSeat)
    {
        var calls = new List<SeatCallView>(MahjongGame.Seats - 2);

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            if (seat == forSeat || seat == pending.FromSeat) continue;

            pending.Declared.TryGetValue(seat, out var call);

            // An outranked seat is in Passed as well, so that has to be read first or the reason a
            // seat is out of the discard is flattened into a plain "no thanks".
            var progress = call switch
            {
                { AwaitingTiles: true } => SeatCallState.Calling,
                not null => SeatCallState.Called,
                _ when pending.Outranked.Contains(seat) => SeatCallState.Outranked,
                _ when pending.Passed.Contains(seat) => SeatCallState.Passed,
                _ => SeatCallState.Waiting,
            };

            calls.Add(new SeatCallView(seat, progress, call?.Kind));
        }

        return calls;
    }

    private static TurnOptionsView? BuildTurnOptions(GameState state, int forSeat)
    {
        if (forSeat < 0 || state.CurrentSeat != forSeat) return null;
        if (state.Phase != GamePhase.AwaitingDiscard) return null;

        var hand = state.Hands[forSeat];

        var secretKangs = hand.Concealed
            .GroupBy(t => t.Tile)
            .Where(g => g.Count() == 4)
            .Select(g => g.Key.Code)
            .ToList();

        var sagasas = hand.Melds
            .Where(m => m.Kind == SetKind.Pung)
            .Select(m => m.BaseTile)
            .Where(face => hand.Has(face))
            .Select(face => face.Code)
            .ToList();

        // Asked of the whole hand, not of how it got here. A pung or chow that finishes the hand
        // leaves 17 tiles with nothing drawn, and that is still a win - so the seat is offered
        // Todas rather than being left with Discard as its only move.
        var canWin = HandAnalyzer.Analyze(hand.Concealed, hand.Melds, state.Joker, state.Rules).IsWin;

        return new TurnOptionsView(CanDiscard: true, canWin, secretKangs, sagasas);
    }

    private static OutcomeView? BuildOutcome(GameState state)
    {
        if (state.Outcome is not { } outcome) return null;

        var breakdown = new List<ScoreLineView>();

        if (outcome.Score is { } score)
        {
            breakdown.Add(new ScoreLineView("Todas", score.BaseUnits));
            breakdown.AddRange(score.Bonuses.Select(b => new ScoreLineView(b.Key.ToString(), b.Value)));
        }

        return new OutcomeView(
            outcome.Reason,
            outcome.WinnerSeat,
            outcome.Score?.TotalUnits ?? 0,
            breakdown,
            outcome.Settlements);
    }
}
