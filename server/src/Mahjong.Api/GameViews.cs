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
public sealed record SeatStateView(
    int Seat,
    string? DisplayName,
    bool IsBot,
    bool IsConnected,
    int ConcealedCount,
    IReadOnlyList<TileView>? Concealed,
    IReadOnlyList<HandGroupView>? Groups,
    IReadOnlyList<MeldView> Melds,
    IReadOnlyList<TileView> Bonus,
    int Balance);

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
public sealed record ClaimPromptView(
    TileView Tile,
    int FromSeat,
    DateTimeOffset DeadlineUtc,
    IReadOnlyList<ClaimKind> YourOptions,
    IReadOnlyList<ClaimCandidateView> Candidates,
    bool YouAnswered);

/// <summary>What the player whose turn it is may do right now.</summary>
public sealed record TurnOptionsView(
    bool CanDiscard,
    bool CanDeclareTodas,
    IReadOnlyList<string> SecretKangFaces,
    IReadOnlyList<string> SagasaFaces);

public sealed record ScoreLineView(string Name, int Units);

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
    OutcomeView? Outcome)
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
    public static PlayerGameView Build(
        GameState state,
        string roomCode,
        int forSeat,
        IReadOnlyDictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> seatInfo)
    {
        var seats = new List<SeatStateView>(MahjongGame.Seats);

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var hand = state.Hands[seat];
            var info = seatInfo.TryGetValue(seat, out var found) ? found : (Name: (string?)null, IsBot: false, IsConnected: false, Balance: 0);

            seats.Add(new SeatStateView(
                seat,
                info.Name,
                info.IsBot,
                info.IsConnected,
                hand.Concealed.Count,
                // The one line that decides whether this game is cheatable.
                seat == forSeat ? hand.Concealed.Select(TileView.Of).ToList() : null,
                // Same rule: the grouping is read off the concealed tiles, so handing it to
                // anyone else would hand them the hand.
                seat == forSeat ? BuildGroups(state, hand) : null,
                hand.Melds.Select(ToView).ToList(),
                hand.Bonus.Select(TileView.Of).ToList(),
                info.Balance));
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
            BuildOutcome(state));
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

        var candidates = MahjongGame.ClaimCandidates(state, pending.Tile, pending.FromSeat, forSeat);
        if (candidates.Count == 0) return null;

        return new ClaimPromptView(
            TileView.Of(pending.Tile),
            pending.FromSeat,
            pending.DeadlineUtc,
            candidates.Select(c => c.Kind).Distinct().ToList(),
            // Described server side, so the button text and the spoken label cannot drift from
            // what the candidate actually is.
            candidates
                .Select(c => new ClaimCandidateView(
                    c.Kind,
                    c.Support.Select(t => t.Id).ToList(),
                    c.Describe(pending.Tile)))
                .ToList(),
            pending.Passed.Contains(forSeat) || pending.Declared.ContainsKey(forSeat));
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

        var canWin = state.JustDrew is not null
            && HandAnalyzer.Analyze(hand.Concealed, hand.Melds, state.Joker, state.Rules).IsWin;

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
