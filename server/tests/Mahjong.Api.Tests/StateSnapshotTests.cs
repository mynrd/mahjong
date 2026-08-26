using Mahjong.Domain;
using Mahjong.Infrastructure;

namespace Mahjong.Api.Tests;

/// <summary>
/// The snapshot in <c>Games.StateJson</c> is what a reconnecting player is rebuilt from, so it has
/// to survive a round trip exactly. A silently dropped field here would not throw: the hand would
/// just come back subtly wrong, with the wrong tiles or the wrong turn.
/// </summary>
public class StateSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static GameState RoundTrip(GameState state) =>
        GameJson.Deserialize<GameState>(GameJson.Serialize(state));

    [Fact]
    public void A_freshly_dealt_hand_survives_a_round_trip()
    {
        var (state, _) = MahjongGame.Deal(RuleOptions.Default, 1, 2, seed: 99, Now);
        var restored = RoundTrip(state);

        Assert.Equal(state.HandNumber, restored.HandNumber);
        Assert.Equal(state.ManoSeat, restored.ManoSeat);
        Assert.Equal(state.Seed, restored.Seed);
        Assert.Equal(state.Joker, restored.Joker);
        Assert.Equal(state.CurrentSeat, restored.CurrentSeat);
        Assert.Equal(state.Phase, restored.Phase);
        Assert.Equal(state.FrontIndex, restored.FrontIndex);
        Assert.Equal(state.BackIndex, restored.BackIndex);
        Assert.Equal(state.JustDrew, restored.JustDrew);

        Assert.Equal(state.Wall.Select(t => t.Id), restored.Wall.Select(t => t.Id));

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            Assert.Equal(
                state.Hands[seat].Concealed.Select(t => t.Id),
                restored.Hands[seat].Concealed.Select(t => t.Id));

            Assert.Equal(
                state.Hands[seat].Bonus.Select(t => t.Id),
                restored.Hands[seat].Bonus.Select(t => t.Id));
        }
    }

    [Fact]
    public void A_bonus_tile_in_a_snapshot_does_not_blow_up_the_serialiser()
    {
        // Tile.PlayableIndex throws for bonus tiles. If tiles were ever serialised by reflecting
        // over their properties instead of through the code converter, the first flower dealt
        // would take the whole hand down.
        var (state, _) = MahjongGame.Deal(RuleOptions.Default, 1, 0, seed: 7, Now);

        Assert.Contains(state.Hands, h => h.Bonus.Count > 0);

        var json = GameJson.Serialize(state);
        Assert.Contains("\"wall\"", json);

        var restored = GameJson.Deserialize<GameState>(json);
        Assert.Equal(
            state.Hands.Sum(h => h.Bonus.Count),
            restored.Hands.Sum(h => h.Bonus.Count));
    }

    [Fact]
    public void House_rules_travel_with_the_snapshot()
    {
        var rules = RuleOptions.Default with
        {
            JokerEnabled = false,
            ChowFromLeftOnly = false,
            ClaimWindowSeconds = 12,
            Scoring = ScoringProfile.Default with
            {
                TodasBase = 7,
                Bonuses = new Dictionary<WinBonus, int>(ScoringProfile.Default.Bonuses)
                {
                    [WinBonus.Escalera] = 33,
                },
            },
        };

        var (state, _) = MahjongGame.Deal(rules, 1, 0, seed: 5, Now);
        var restored = RoundTrip(state);

        Assert.False(restored.Rules.JokerEnabled);
        Assert.False(restored.Rules.ChowFromLeftOnly);
        Assert.Equal(12, restored.Rules.ClaimWindowSeconds);
        Assert.Equal(7, restored.Rules.Scoring.TodasBase);
        Assert.Equal(33, restored.Rules.Scoring.Bonuses[WinBonus.Escalera]);
    }

    [Fact]
    public void Enum_keyed_scoring_values_are_stored_by_name_not_by_position()
    {
        // Storing WinBonus.Escalera as "0" would silently repoint every stored room's scoring
        // table the first time a member was inserted anywhere but the end of the enum.
        var json = GameJson.Serialize(RuleOptions.Default);

        Assert.Contains("\"Escalera\"", json);
        Assert.Contains("\"SecretKang\"", json);
    }

    [Fact]
    public void Rules_stored_before_a_rule_was_retired_still_load()
    {
        // Rooms created before winds and dragons became hand tiles have "ThirteenFlowers" sitting
        // in their stored ambition table. That name no longer maps to a member, and refusing to
        // read it would lock every one of those rooms out the moment somebody connects.
        const string json = "{\"scoring\":{\"todasBase\":2,\"ambitions\":{\"NoFlowers\":1,\"ThirteenFlowers\":1,\"Kang\":1},\"bonuses\":{\"Escalera\":4,\"Retired\":9}}}";

        var rules = GameJson.Deserialize<RuleOptions>(json);

        Assert.Equal(2, rules.Scoring.Ambitions.Count);
        Assert.Equal(1, rules.Scoring.Ambitions[Ambition.NoFlowers]);
        Assert.Equal(1, rules.Scoring.Ambitions[Ambition.Kang]);
        Assert.Equal(4, Assert.Single(rules.Scoring.Bonuses).Value);
    }

    [Fact]
    public void A_hand_in_the_middle_of_a_claim_window_survives_a_round_trip()
    {
        var (state, _) = MahjongGame.Deal(RuleOptions.Default with { JokerEnabled = false }, 1, 0, seed: 4242, Now);

        // Play until somebody's discard is contested, which is what a restart has to survive.
        var rng = new Random(4242);
        var guard = 0;
        while (state.Phase != GamePhase.AwaitingClaims && state.Phase != GamePhase.HandOver && guard++ < 500)
        {
            if (state.Phase == GamePhase.AwaitingDraw)
            {
                MahjongGame.Draw(state, state.CurrentSeat, Now);
            }
            else
            {
                var hand = state.Hands[state.CurrentSeat].Concealed;
                MahjongGame.Discard(state, state.CurrentSeat, hand[rng.Next(hand.Count)].Id, Now);
            }
        }

        Assert.Equal(GamePhase.AwaitingClaims, state.Phase);

        var restored = RoundTrip(state);

        Assert.NotNull(restored.Pending);
        Assert.Equal(state.Pending!.Tile.Id, restored.Pending!.Tile.Id);
        Assert.Equal(state.Pending.FromSeat, restored.Pending.FromSeat);
        Assert.Equal(state.Pending.DeadlineUtc, restored.Pending.DeadlineUtc);
        Assert.Equal(state.Pending.Passed.Order(), restored.Pending.Passed.Order());
    }

    [Fact]
    public void A_claim_window_with_no_deadline_survives_a_round_trip()
    {
        // The serialiser leaves nulls out, and DeadlineUtc is `required`, so a window nothing is
        // timing wrote a snapshot that would not read back at all: the whole hand failed to load
        // with "missing required properties". Every discard a bot makes has one, so this took out
        // reconnecting mid-hand and every replay of a hand played against bots.
        var (state, _) = MahjongGame.Deal(RuleOptions.Default with { JokerEnabled = false }, 1, 0, seed: 5, Now);

        state.BotSeats.Add(0);
        MahjongGame.Discard(state, 0, state.Hands[0].Concealed[0].Id, Now);

        Assert.Null(state.Pending!.DeadlineUtc);

        var restored = RoundTrip(state);

        Assert.NotNull(restored.Pending);
        Assert.Null(restored.Pending!.DeadlineUtc);
        Assert.Equal(state.Pending.Tile.Id, restored.Pending.Tile.Id);
    }

    [Fact]
    public void A_finished_hand_keeps_its_score_breakdown_through_a_round_trip()
    {
        var (state, _) = MahjongGame.Deal(RuleOptions.Default with { JokerEnabled = false }, 1, 0, seed: 11, Now);

        // Force a completed hand into seat 0 and declare on it.
        var hand = state.Hands[0];
        hand.Concealed.Clear();
        hand.Concealed.AddRange(TileNotation.ParseRefs("123d 456d 789d 111b 222b 33c"));
        state.JustDrew = hand.Concealed[^1];

        MahjongGame.DeclareTodasOnDraw(state, 0);
        Assert.Equal(GamePhase.HandOver, state.Phase);

        var restored = RoundTrip(state);

        Assert.NotNull(restored.Outcome);
        Assert.Equal(state.Outcome!.Reason, restored.Outcome!.Reason);
        Assert.Equal(state.Outcome.WinnerSeat, restored.Outcome.WinnerSeat);
        Assert.Equal(state.Outcome.Score!.TotalUnits, restored.Outcome.Score!.TotalUnits);
        Assert.Equal(
            state.Outcome.Score.Bonuses.OrderBy(b => b.Key),
            restored.Outcome.Score.Bonuses.OrderBy(b => b.Key));
        Assert.Equal(
            state.Outcome.Settlements.Select(s => (s.Seat, s.Delta)),
            restored.Outcome.Settlements.Select(s => (s.Seat, s.Delta)));
    }
}
