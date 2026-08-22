using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Infrastructure;

namespace Mahjong.Api.Tests;

/// <summary>
/// A replay is the one place where showing all four hands is correct, so these tests pull in two
/// directions at once: the frame has to reveal every seat, and it still has to keep the wall out.
/// The wall matters because its order is every future draw, and a replay of hand 3 must not become
/// a cheat sheet for hand 4 if somebody ever wires the two together.
/// </summary>
public class ReplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<int, (string Name, bool IsBot, int Balance)> Seats = new()
    {
        [0] = ("Mynard", false, 11),
        [1] = ("Tito Ben", true, -5),
        [2] = ("Ate Rose", true, -7),
        [3] = ("Kuya Jun", true, 1),
    };

    private static readonly Dictionary<int, string> Names =
        Seats.ToDictionary(s => s.Key, s => s.Value.Name);

    private static GameState Dealt(int seed = 4242) =>
        MahjongGame.Deal(RuleOptions.Default, 1, 0, seed, Now).State;

    private static ReplayFrameView Frame(GameState state, string caption = "Tiles dealt") =>
        ReplayViewBuilder.Build(state, index: 0, afterSeq: 1, caption, Seats);

    /// <summary>
    /// A hand where the next thing that happens is a draw. Straight after the deal the mano is
    /// holding 17 tiles and the phase is AwaitingDiscard, so nobody can draw until it has thrown
    /// one and the claim window on it has closed.
    /// </summary>
    private static GameState PastTheOpeningDiscard()
    {
        var state = Dealt();

        MahjongGame.Discard(state, state.CurrentSeat, state.Hands[state.CurrentSeat].Concealed[0].Id, Now);
        MahjongGame.ExpireClaimWindow(state, Now.AddMinutes(1));

        Assert.Equal(GamePhase.AwaitingDraw, state.Phase);
        return state;
    }

    // ------------------------------------------------------------------ reveal

    [Fact]
    public void Every_seat_is_face_up()
    {
        var state = Dealt();
        var frame = Frame(state);

        Assert.Equal(MahjongGame.Seats, frame.Seats.Count);

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            Assert.Equal(
                state.Hands[seat].Concealed.Select(t => t.Id),
                frame.Seats[seat].Concealed.Select(t => t.Id));

            Assert.Equal(
                state.Hands[seat].Bonus.Select(t => t.Id),
                frame.Seats[seat].Bonus.Select(t => t.Id));
        }
    }

    [Fact]
    public void Seat_names_balances_and_bot_flags_come_through()
    {
        var frame = Frame(Dealt());

        Assert.Equal("Mynard", frame.Seats[0].DisplayName);
        Assert.False(frame.Seats[0].IsBot);
        Assert.Equal(11, frame.Seats[0].Balance);

        Assert.True(frame.Seats[2].IsBot);
        Assert.Equal(-7, frame.Seats[2].Balance);
    }

    [Fact]
    public void A_seat_nobody_is_sitting_in_still_renders()
    {
        // A room whose players were never loaded must not take the replay down with a key error.
        var frame = ReplayViewBuilder.Build(Dealt(), 0, 1, "Tiles dealt", new Dictionary<int, (string, bool, int)>());

        Assert.All(frame.Seats, seat => Assert.Null(seat.DisplayName));
        Assert.All(frame.Seats, seat => Assert.NotEmpty(seat.Concealed));
    }

    // ------------------------------------------------------------------ redaction that survives

    [Fact]
    public void The_wall_never_reaches_the_frame()
    {
        var state = Dealt();

        // Every tile id that a seat is entitled to have on the table: their own tiles, their melds,
        // their bonus tiles, and anything already discarded. Everything else is still in the wall.
        var accounted = state.Hands
            .SelectMany(h => h.Concealed.Concat(h.Bonus).Concat(h.Melds.SelectMany(m => m.Tiles)))
            .Concat(state.Discards.Select(d => d.Tile))
            .Select(t => t.Id)
            .ToHashSet();

        var undrawn = state.Wall.Select(t => t.Id).Where(id => !accounted.Contains(id)).ToHashSet();
        Assert.NotEmpty(undrawn);

        foreach (var id in TileIdsIn(Frame(state)))
            Assert.DoesNotContain(id, undrawn);
    }

    [Fact]
    public void A_replay_frame_carries_no_field_called_wall()
    {
        // Belt and braces against somebody adding the wall back to ReplayFrameView later: the id
        // check above would still pass on the frame that starts a hand if the wall were serialised
        // under a name the test did not look at.
        var json = JsonSerializer.Serialize(Frame(Dealt()), GameJson.Options);

        Assert.DoesNotContain("\"wall\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"frontIndex\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"backIndex\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_live_view_is_still_redacted()
    {
        // The replay builder exists so that GameViewBuilder never needs a "show everything" switch.
        // If one is ever added, this fails alongside RedactionTests.
        var liveSeats = Seats.ToDictionary(
            s => s.Key,
            s => (s.Value.Name, s.Value.IsBot, IsConnected: true, s.Value.Balance));

        var view = GameViewBuilder.Build(Dealt(), "ABC123", forSeat: 1, liveSeats);

        Assert.NotNull(view.Seats[1].Concealed);
        Assert.Null(view.Seats[0].Concealed);
        Assert.Null(view.Seats[2].Concealed);
        Assert.Null(view.Seats[3].Concealed);
    }

    // ------------------------------------------------------------------ what the frame tracks

    [Fact]
    public void A_discard_shows_up_in_the_frame_that_follows_it()
    {
        // The mano is dealt 17 tiles, so a hand opens on AwaitingDiscard with nothing to draw.
        var state = Dealt();

        var thrown = state.Hands[state.CurrentSeat].Concealed[0];
        MahjongGame.Discard(state, state.CurrentSeat, thrown.Id, Now);

        var frame = Frame(state, "discard");

        Assert.Contains(frame.Discards, d => d.Tile.Id == thrown.Id);
        Assert.Equal(state.TilesRemaining, frame.TilesRemaining);
        Assert.Equal(state.Phase, frame.Phase);
    }

    [Fact]
    public void There_is_no_outcome_until_the_hand_ends()
    {
        Assert.Null(Frame(Dealt()).Outcome);
    }

    [Fact]
    public void A_finished_hand_carries_its_outcome()
    {
        var state = PastTheOpeningDiscard();

        // Drive the wall dry rather than trying to force a win: the drawn ending is the one that
        // can be reached from any seed.
        state.FrontIndex = state.BackIndex + 1;
        MahjongGame.Draw(state, state.CurrentSeat);

        var frame = Frame(state, "wall out");

        Assert.NotNull(frame.Outcome);
        Assert.Equal(HandEndReason.WallExhausted, frame.Outcome!.Reason);
        Assert.Null(frame.Outcome.WinnerSeat);
    }

    // ------------------------------------------------------------------ arrange

    /// <summary>
    /// A hand forced into a five-bahay-plus-pair win, declared on the draw. Same shape as the one
    /// in <c>StateSnapshotTests</c>: the joker is off so the tiles read as exactly what they are.
    /// </summary>
    private static GameState WonBySeatZero()
    {
        var (state, _) = MahjongGame.Deal(RuleOptions.Default with { JokerEnabled = false }, 1, 0, seed: 11, Now);

        var hand = state.Hands[0];
        hand.Concealed.Clear();
        hand.Concealed.AddRange(TileNotation.ParseRefs("123d 456d 789d 111b 222b 33c"));
        state.JustDrew = hand.Concealed[^1];

        MahjongGame.DeclareTodasOnDraw(state, 0);
        Assert.Equal(GamePhase.HandOver, state.Phase);

        return state;
    }

    [Fact]
    public void The_winning_hand_is_grouped_into_the_sets_it_won_with()
    {
        var frame = Frame(WonBySeatZero(), "todas");
        var groups = frame.Seats[0].Groups;

        Assert.Equal(
            HandAnalyzer.SetsPerHand,
            groups.Count(g => g.Kind is HandGroupKind.Chow or HandGroupKind.Pung or HandGroupKind.Kang));

        Assert.Single(groups, g => g.Kind == HandGroupKind.Pair);
    }

    [Fact]
    public void Grouping_uses_every_concealed_tile_exactly_once()
    {
        // The blocks replace the flat row on screen, so a tile the arranger dropped would vanish
        // from the hand and one it repeated would show twice.
        var frame = Frame(WonBySeatZero(), "todas");
        var seat = frame.Seats[0];

        var grouped = seat.Groups.SelectMany(g => g.Tiles).Select(t => t.Id).ToList();

        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.Equal(seat.Concealed.Select(t => t.Id).Order(), grouped.Order());
    }

    [Fact]
    public void Every_seat_is_grouped_once_the_hand_is_over()
    {
        // The reader is trying to work out how somebody won, and the three hands that did not win
        // are half of that answer.
        var frame = Frame(WonBySeatZero(), "todas");

        Assert.All(frame.Seats, seat => Assert.NotEmpty(seat.Groups));
    }

    [Fact]
    public void A_hand_still_being_played_carries_no_groups()
    {
        // Arranging four hands on every frame of a hand is several hundred searches nobody reads,
        // so the frames before the ending send none and the client hides the toggle.
        Assert.All(Frame(Dealt()).Seats, seat => Assert.Empty(seat.Groups));
        Assert.All(Frame(PastTheOpeningDiscard()).Seats, seat => Assert.Empty(seat.Groups));
    }

    // ------------------------------------------------------------------ the action log

    [Fact]
    public void An_event_is_logged_as_the_event_it_actually_is()
    {
        // Events are handed around as GameEvent, and System.Text.Json writes the declared type, so
        // serialising one through the base type stores {"seat":0} and drops the tile. Every payload
        // in the action log was that shape until SerializeEvent, and a replay caption reading one
        // back does not throw - it just says the wrong tile. Hence a test on the exact JSON.
        var state = Dealt();
        var thrown = state.Hands[0].Concealed[0];

        GameEvent discarded = MahjongGame.Discard(state, 0, thrown.Id, Now)
            .OfType<TileDiscarded>()
            .Single();

        var json = GameJson.SerializeEvent(discarded);

        Assert.Contains("\"tile\"", json);
        Assert.Equal(thrown.Id, GameJson.Deserialize<TileDiscarded>(json).Tile.Id);
    }

    [Fact]
    public void Every_kind_of_event_survives_the_log()
    {
        // One assertion over the whole deal rather than a case per event type: what matters is that
        // no event ends up stored as nothing but its seat.
        var (state, dealEvents) = MahjongGame.Deal(RuleOptions.Default, 1, 0, seed: 4242, Now);

        var thrown = state.Hands[0].Concealed[0];
        var all = dealEvents
            .Concat(MahjongGame.Discard(state, 0, thrown.Id, Now))
            .Concat(MahjongGame.ExpireClaimWindow(state, Now.AddMinutes(1)))
            .ToList();

        // TurnChanged carries a phase and nothing else, and ClaimWindowClosed is the only other
        // event whose payload could legitimately be thin, so they are the two exceptions.
        var thin = all
            .Where(e => e is not (TurnChanged or ClaimWindowClosed))
            .Where(e => GameJson.SerializeEvent(e) is var json && !json.Contains(',') && json.Contains("seat"))
            .Select(e => e.GetType().Name)
            .ToList();

        Assert.Empty(thin);
    }

    // ------------------------------------------------------------------ captions

    [Fact]
    public void The_dealing_events_read_as_english()
    {
        var (_, events) = MahjongGame.Deal(RuleOptions.Default, 1, 0, seed: 4242, Now);
        var caption = ReplayCaption.For(Log(events), Names);

        Assert.Contains("Tiles dealt", caption);
        Assert.Contains("Joker is", caption);
    }

    [Fact]
    public void A_discard_is_captioned_with_the_player_and_the_tile()
    {
        var state = Dealt();
        var thrown = state.Hands[0].Concealed[0];

        var caption = ReplayCaption.For(Log(MahjongGame.Discard(state, 0, thrown.Id, Now)), Names);

        Assert.Contains("Mynard discarded", caption);
        Assert.Contains(Spoken(thrown.Tile), caption);
    }

    [Fact]
    public void A_draw_is_captioned_with_the_player_and_the_tile()
    {
        var state = PastTheOpeningDiscard();
        var seat = state.CurrentSeat;

        var caption = ReplayCaption.For(Log(MahjongGame.Draw(state, seat)), Names);

        Assert.Contains($"{Names[seat]} drew", caption);
    }

    [Fact]
    public void Bookkeeping_events_do_not_produce_a_caption_of_their_own()
    {
        // TurnChanged fires after most moves. If it were captioned, every other line would read
        // "the turn changed" and the useful ones would be buried.
        var caption = ReplayCaption.For(Log([new TurnChanged(GamePhase.AwaitingDraw) { Seat = 2 }]), Names);

        Assert.Equal("Play moves on", caption);
    }

    [Fact]
    public void An_unknown_event_type_is_still_named()
    {
        var action = new GameAction { Seq = 1, Seat = 1, ActionType = "SomethingNew", PayloadJson = "{}" };

        Assert.Equal("Tito Ben: SomethingNew", ReplayCaption.For([action], Names));
    }

    [Fact]
    public void An_unreadable_payload_falls_back_to_the_action_name()
    {
        // A caption is never worth failing a replay over, so a payload that cannot be read has to
        // degrade rather than throw.
        var action = new GameAction { Seq = 1, Seat = 3, ActionType = nameof(TileDiscarded), PayloadJson = "{oops" };

        Assert.Equal("Kuya Jun: TileDiscarded", ReplayCaption.For([action], Names));
    }

    [Fact]
    public void A_seat_with_no_name_is_still_identified()
    {
        var action = new GameAction { Seq = 1, Seat = 2, ActionType = nameof(TileDiscarded), PayloadJson = "{oops" };

        Assert.Equal("Seat 2: TileDiscarded", ReplayCaption.For([action], new Dictionary<int, string>()));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Turns events into log rows the same way <c>GameService.RecordAsync</c> does, so the captions
    /// are read back out of exactly the JSON that gets stored.
    /// </summary>
    private static List<GameAction> Log(IReadOnlyList<GameEvent> events) =>
        events.Select((evt, i) => new GameAction
        {
            Seq = i + 1,
            Seat = evt.Seat,
            ActionType = evt.GetType().Name,
            PayloadJson = GameJson.SerializeEvent(evt),
        }).ToList();

    private static string Spoken(Tile tile) => tile.Code[0] switch
    {
        'D' => $"{tile.Code[1..]} dots",
        'B' => $"{tile.Code[1..]} bamboo",
        'C' => $"{tile.Code[1..]} characters",
        _ => tile.Code,
    };

    private static IEnumerable<int> TileIdsIn(ReplayFrameView frame)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(frame, GameJson.Options));
        return Ids(document.RootElement).ToList();
    }

    private static IEnumerable<int> Ids(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.Number)
                        yield return property.Value.GetInt32();

                    foreach (var id in Ids(property.Value)) yield return id;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var id in Ids(item))
                        yield return id;

                break;
        }
    }
}
