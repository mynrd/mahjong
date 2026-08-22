using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Infrastructure;

namespace Mahjong.Api;

/// <summary>One seat in a replay. Every tile is face up, which is the whole point of a replay.</summary>
/// <param name="Groups">
/// The concealed tiles read as blocks, for the Arrange toggle. Only filled in on frames where the
/// hand has ended - that is the frame where "how did that win" is the question being asked, and
/// arranging all four hands on all several hundred frames of a hand would be several hundred times
/// the search for no reader.
/// </param>
public sealed record ReplaySeatView(
    int Seat,
    string? DisplayName,
    bool IsBot,
    IReadOnlyList<TileView> Concealed,
    IReadOnlyList<HandGroupView> Groups,
    IReadOnlyList<MeldView> Melds,
    IReadOnlyList<TileView> Bonus,
    int Balance);

/// <summary>One step of a finished hand.</summary>
/// <param name="Index">Position in the frame list, from 0, so the client can label "12 of 387".</param>
/// <param name="AfterSeq">Seq of the last logged action this frame includes.</param>
/// <param name="Caption">What happened to produce it, e.g. "Ate Rose discarded 5 dots".</param>
public sealed record ReplayFrameView(
    int Index,
    int AfterSeq,
    string Caption,
    int CurrentSeat,
    GamePhase Phase,
    int TilesRemaining,
    IReadOnlyList<ReplaySeatView> Seats,
    IReadOnlyList<DiscardView> Discards,
    OutcomeView? Outcome);

/// <summary>One finished hand, as listed on the replay index.</summary>
public sealed record ReplayListItemView(
    int HandNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int ManoSeat,
    string? Joker,
    int? WinnerSeat,
    string? WinnerName,
    string Reason,
    int TotalUnits,
    int FrameCount);

/// <summary>Everything needed to render one replay.</summary>
public sealed record ReplayView(
    string RoomCode,
    int HandNumber,
    int ManoSeat,
    string? Joker,
    IReadOnlyList<ReplayFrameView> Frames);

/// <summary>What the caller gets for knowing the room password.</summary>
public sealed record ReplayUnlockResponse(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Turns stored <see cref="GameState"/> snapshots into frames with every hand face up.
///
/// Deliberately a separate builder from <see cref="GameViewBuilder"/> rather than a "reveal
/// everything" flag on it. That builder decides whether the live game is cheatable and
/// <c>RedactionTests</c> exists to hold it to that; a flag would put a switch for showing all four
/// hands inside the live broadcast path, one bad call site away from being flipped. This type is
/// only ever reached from the replay endpoints, and only for a hand that has already finished.
///
/// The wall is left out. A replay does not need it, and leaving it out means the order of the
/// undrawn tiles never reaches a browser at all.
/// </summary>
public static class ReplayViewBuilder
{
    public static ReplayFrameView Build(
        GameState state,
        int index,
        int afterSeq,
        string caption,
        IReadOnlyDictionary<int, (string Name, bool IsBot, int Balance)> seatInfo)
    {
        var seats = new List<ReplaySeatView>(MahjongGame.Seats);
        var ended = state.Outcome is not null;

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var hand = state.Hands[seat];
            var info = seatInfo.TryGetValue(seat, out var found)
                ? found
                : (Name: (string?)null, IsBot: false, Balance: 0);

            seats.Add(new ReplaySeatView(
                seat,
                info.Name,
                info.IsBot,
                hand.Concealed.Select(TileView.Of).ToList(),
                ended ? BuildGroups(state, hand, seat) : [],
                hand.Melds.Select(ToView).ToList(),
                hand.Bonus.Select(TileView.Of).ToList(),
                info.Balance));
        }

        return new ReplayFrameView(
            index,
            afterSeq,
            caption,
            state.CurrentSeat,
            state.Phase,
            state.TilesRemaining,
            seats,
            state.Discards.Select(d => new DiscardView(d.Seat, TileView.Of(d.Tile), d.Claimed)).ToList(),
            BuildOutcome(state));
    }

    /// <summary>
    /// The winner's hand is laid out as the reading the scorer paid on, so the frame that ends the
    /// hand answers "why did that win" with the same five bahay and pair the money came from. A
    /// siete pares win is where this matters most: <see cref="HandArranger"/> would prefer a pung
    /// to two of the seven pairs and lay the hand out by what the tiles could do rather than by how
    /// it won.
    ///
    /// Everyone else gets <see cref="HandArranger.Arrange"/>, the same arrangement the live table
    /// shows its owner under Auto Arrange, so a hand does not read one way while it is being played
    /// and another way in the replay. The winner falls back to it too if the reading does not map
    /// onto the tiles in hand, which would mean drawing a hand with tiles missing.
    /// </summary>
    private static IReadOnlyList<HandGroupView> BuildGroups(GameState state, PlayerHand hand, int seat)
    {
        var joker = state.Rules.JokerEnabled ? state.Joker : null;

        IReadOnlyList<HandGroup> groups =
            state.Outcome is { WinnerSeat: { } winner, Score: { } score } && winner == seat
                ? HandArranger.FromReading(score.Reading, hand.Concealed, hand.Melds, joker)
                : [];

        if (groups.Count == 0)
            groups = HandArranger.Arrange(hand.Concealed, hand.Melds, state.Joker, state.Rules);

        return groups
            .Select(g => new HandGroupView(
                g.Kind,
                g.Tiles.Select(TileView.Of).ToList(),
                g.Needs.Select(t => t.Code).ToList(),
                g.JokersUsed))
            .ToList();
    }

    private static MeldView ToView(ExposedMeld meld) => new(
        meld.Kind,
        meld.Tiles.Select(TileView.Of).ToList(),
        meld.Concealed,
        meld.ClaimedFromSeat,
        meld.FromSagasa);

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

/// <summary>
/// Turns the logged actions covered by a frame into one line of English.
///
/// Written on the server, next to the events themselves, so the caption cannot drift from what the
/// action actually was the way a switch statement in the client would.
/// </summary>
public static class ReplayCaption
{
    /// <summary>
    /// Events that are real but not worth a line. <c>TurnChanged</c> fires after most moves, so
    /// captioning it would bury the useful lines under "the turn changed" every other frame.
    /// </summary>
    private static readonly HashSet<string> Silent =
    [
        nameof(ClaimWindowOpened),
        nameof(TurnChanged),
    ];

    public static string For(IReadOnlyList<GameAction> actions, IReadOnlyDictionary<int, string> names)
    {
        var lines = actions.Select(a => Describe(a, names)).Where(line => line is not null).ToList();

        if (lines.Count > 0) return string.Join(", ", lines);
        if (actions.Count == 0) return "Start of hand";

        // Nothing worth a line, but nothing went wrong either. Naming the raw event type is only
        // right when the type was one this switch does not know about.
        return actions.All(a => Silent.Contains(a.ActionType))
            ? "Play moves on"
            : Fallback(actions[^1], names);
    }

    private static string Who(int seat, IReadOnlyDictionary<int, string> names) =>
        seat < 0 ? "Table" : names.TryGetValue(seat, out var name) ? name : $"Seat {seat}";

    private static string? Describe(GameAction action, IReadOnlyDictionary<int, string> names)
    {
        var who = Who(action.Seat, names);

        // The payload is the serialised event, so it is read back through the same options it was
        // written with. Anything unreadable falls through to the action name rather than throwing:
        // a caption is not worth failing a replay over.
        try
        {
            return action.ActionType switch
            {
                nameof(HandDealt) => "Tiles dealt",
                nameof(JokerChosen) => $"Joker is {Face(Payload<JokerChosen>(action).Joker)}",
                nameof(TileDrawn) => Payload<TileDrawn>(action) switch
                {
                    { Replacement: true } drawn => $"{who} drew {Face(drawn.Tile.Tile)} to replace it",
                    var drawn => $"{who} drew {Face(drawn.Tile.Tile)}",
                },
                nameof(BonusExposed) => $"{who} turned up {Face(Payload<BonusExposed>(action).Tile.Tile)}",
                nameof(TileDiscarded) => $"{who} discarded {Face(Payload<TileDiscarded>(action).Tile.Tile)}",
                nameof(MeldFormed) => $"{who} took {Payload<MeldFormed>(action).Meld.Kind.ToString().ToLowerInvariant()}",
                nameof(AmbitionEarned) => $"{who} scored {Payload<AmbitionEarned>(action).Ambition}",
                nameof(HandEnded) => Ending(Payload<HandEnded>(action), names),
                nameof(ClaimWindowClosed) => "Nobody took it",

                // Bookkeeping the reader does not need a line for. The frame these land on is
                // captioned by whatever else happened in the same move.
                _ when Silent.Contains(action.ActionType) => null,

                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return Fallback(action, names);
        }
    }

    private static string Ending(HandEnded ended, IReadOnlyDictionary<int, string> names) =>
        ended.Outcome switch
        {
            { Reason: HandEndReason.WallExhausted } => "Wall ran out, nobody won",
            { WinnerSeat: { } seat, Score: { } score } => $"{Who(seat, names)} won, {score.TotalUnits} units",
            { WinnerSeat: { } seat } => $"{Who(seat, names)} won",
            _ => "Hand over",
        };

    private static string Fallback(GameAction action, IReadOnlyDictionary<int, string> names) =>
        $"{Who(action.Seat, names)}: {action.ActionType}";

    private static T Payload<T>(GameAction action) => GameJson.Deserialize<T>(action.PayloadJson);

    /// <summary>
    /// The spoken name of a tile, matching the labels the web client reads out, so a caption and a
    /// screen reader say the same thing about the same tile.
    /// </summary>
    private static string Face(Tile tile)
    {
        var code = tile.Code;
        var rank = code.Length > 1 && int.TryParse(code[1..], out var parsed) ? parsed : 0;

        return code[0] switch
        {
            'D' => $"{rank} dots",
            'B' => $"{rank} bamboo",
            'C' => $"{rank} characters",
            'W' => rank is >= 1 and <= 4 ? $"{Winds[rank]} wind" : code,
            'R' => rank is >= 1 and <= 3 ? Dragons[rank] : code,
            'F' => rank is >= 1 and <= 4 ? $"{Flowers[rank]} flower" : code,
            'S' => rank is >= 1 and <= 4 ? $"{Seasons[rank]} season" : code,
            _ => code,
        };
    }

    private static readonly string[] Winds = ["", "east", "south", "west", "north"];
    private static readonly string[] Dragons = ["", "red dragon", "green dragon", "white dragon"];
    private static readonly string[] Flowers = ["", "plum", "orchid", "chrysanthemum", "bamboo"];
    private static readonly string[] Seasons = ["", "spring", "summer", "autumn", "winter"];
}
