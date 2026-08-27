using System.Text.Json;
using Mahjong.Api;
using Mahjong.Domain;
using Mahjong.Infrastructure;

namespace Mahjong.Api.Tests;

/// <summary>
/// The tests that stop the game being cheatable.
///
/// The server holds a <see cref="GameState"/> containing every player's tiles and the exact order
/// of the wall. What goes over the wire is a <see cref="PlayerGameView"/> built for one seat. If
/// those two ever get conflated - one DTO reused for both storage and transport, a convenience
/// property added to the wrong type - anyone can open devtools and read the table. So rather than
/// checking a couple of fields, these tests take every tile id that appears anywhere in the
/// serialised view and require it to be one this player is entitled to see.
/// </summary>
public class RedactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<int, (string Name, bool IsBot, bool IsConnected, int Balance)> Seats = new()
    {
        [0] = ("Mynard", false, true, 0),
        [1] = ("Tito Ben", false, true, 0),
        [2] = ("Ate Rose", false, true, 0),
        [3] = ("Kuya Jun", true, true, 0),
    };

    private static GameState Dealt(int seed = 4242) =>
        MahjongGame.Deal(RuleOptions.Default, 1, 0, seed, Now).State;

    [Fact]
    public void A_player_sees_their_own_tiles()
    {
        var state = Dealt();
        var view = GameViewBuilder.Build(state, "ABC123", forSeat: 1, Seats);

        var mine = view.Seats[1];
        Assert.NotNull(mine.Concealed);
        Assert.Equal(state.Hands[1].Concealed.Count, mine.Concealed!.Count);
        Assert.Equal(
            state.Hands[1].Concealed.Select(t => t.Id).Order(),
            mine.Concealed.Select(t => t.Id).Order());
    }

    [Fact]
    public void A_player_never_sees_another_seats_concealed_tiles()
    {
        var state = Dealt();
        var view = GameViewBuilder.Build(state, "ABC123", forSeat: 1, Seats);

        foreach (var seat in new[] { 0, 2, 3 })
        {
            Assert.Null(view.Seats[seat].Concealed);

            // Auto Arrange's grouping is read off the concealed tiles, so it leaks exactly the
            // same thing and has to be withheld the same way.
            Assert.Null(view.Seats[seat].Groups);

            // The count is still sent, because the table needs to show how many tiles each player
            // holds. A count gives nothing away.
            Assert.Equal(state.Hands[seat].Concealed.Count, view.Seats[seat].ConcealedCount);
        }
    }

    [Fact]
    public void A_revealed_hand_is_still_withheld_while_the_hand_is_being_played()
    {
        var state = Dealt();

        // Nothing can put a seat in here mid-hand - the request is refused before it gets this far.
        // Checked anyway: this builder is the last thing between the state and the wire, and it is
        // not entitled to assume the caller got the phase right.
        var view = GameViewBuilder.Build(state, "ABC123", forSeat: 1, Seats, new HashSet<int> { 0 });

        Assert.Null(view.Seats[0].Concealed);
        Assert.False(view.Seats[0].Revealed);
    }

    [Fact]
    public void A_seat_that_shows_its_hand_after_the_hand_is_over_is_seen_by_everybody()
    {
        var state = Dealt();
        state.Phase = GamePhase.HandOver;

        var view = GameViewBuilder.Build(state, "ABC123", forSeat: 1, Seats, new HashSet<int> { 0 });

        var shown = view.Seats[0];
        Assert.True(shown.Revealed);
        Assert.NotNull(shown.Concealed);
        Assert.Equal(
            state.Hands[0].Concealed.Select(t => t.Id).Order(),
            shown.Concealed!.Select(t => t.Id).Order());

        // One seat showing says nothing about the other two, who did not.
        foreach (var seat in new[] { 2, 3 })
        {
            Assert.False(view.Seats[seat].Revealed);
            Assert.Null(view.Seats[seat].Concealed);
        }

        // Showing your tiles is not showing how you had them arranged: the grouping is read off the
        // hand for its owner's own screen and is nobody else's business.
        Assert.Null(shown.Groups);
    }

    [Fact]
    public void A_player_gets_their_own_hand_grouped_for_auto_arrange()
    {
        var state = Dealt();
        var groups = GameViewBuilder.Build(state, "ABC123", forSeat: 1, Seats).Seats[1].Groups;

        Assert.NotNull(groups);

        // Every tile in hand appears in exactly one block, so the layout can be built from the
        // blocks alone without a tile going missing or being drawn twice.
        Assert.Equal(
            state.Hands[1].Concealed.Select(t => t.Id).Order(),
            groups!.SelectMany(g => g.Tiles).Select(t => t.Id).Order());
    }

    [Fact]
    public void No_tile_id_appears_in_a_serialised_view_that_the_player_is_not_entitled_to_see()
    {
        var state = Dealt();

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var view = GameViewBuilder.Build(state, "ABC123", seat, Seats);
            var json = JsonSerializer.Serialize(view, GameJson.Options);

            var allowed = AllowedTileIds(state, seat);
            var leaked = TileIdsIn(json).Except(allowed).ToList();

            Assert.True(leaked.Count == 0,
                $"Seat {seat} was sent tile id(s) it should not see: {string.Join(", ", leaked)}");
        }
    }

    [Fact]
    public void The_wall_is_never_serialised_into_a_view()
    {
        var state = Dealt();
        var json = JsonSerializer.Serialize(
            GameViewBuilder.Build(state, "ABC123", 0, Seats), GameJson.Options);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("wall", out _));

        // Only the count of what is left, which every player can see by looking at the table.
        Assert.Equal(state.TilesRemaining, document.RootElement.GetProperty("tilesRemaining").GetInt32());
    }

    [Fact]
    public void The_view_carries_no_undrawn_tile_from_the_wall()
    {
        var state = Dealt();
        var undrawn = state.Wall
            .Skip(state.FrontIndex)
            .Take(state.BackIndex - state.FrontIndex + 1)
            .Select(t => t.Id)
            .ToHashSet();

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var json = JsonSerializer.Serialize(
                GameViewBuilder.Build(state, "ABC123", seat, Seats), GameJson.Options);

            Assert.Empty(TileIdsIn(json).Where(undrawn.Contains));
        }
    }

    [Fact]
    public void A_spectator_view_shows_nobodys_hand()
    {
        var state = Dealt();
        var view = GameViewBuilder.Build(state, "ABC123", forSeat: -1, Seats);

        Assert.All(view.Seats, s => Assert.Null(s.Concealed));
        Assert.All(view.Seats, s => Assert.Null(s.Groups));
        Assert.Null(view.YourTurn);
        Assert.Null(view.Claim);
    }

    [Fact]
    public void A_claim_prompt_only_ever_names_tiles_the_receiving_seat_holds()
    {
        // The prompt is the one place a view hands a seat a list of tile ids that is not simply
        // its own hand, so it gets its own check.
        var state = OpenClaimWindow();

        for (var seat = 0; seat < MahjongGame.Seats; seat++)
        {
            var claim = GameViewBuilder.Build(state, "ABC123", seat, Seats).Claim;
            if (claim is null) continue;

            var mine = state.Hands[seat].Concealed.Select(t => t.Id).ToHashSet();

            Assert.All(claim.Candidates, c => Assert.All(c.TileIds, id => Assert.Contains(id, mine)));

            // And the kinds offered are exactly the kinds the candidates carry.
            Assert.Equal(claim.YourOptions, claim.Candidates.Select(c => c.Kind).Distinct());
        }
    }

    /// <summary>Plays deals forward until one of them opens a claim window on the discard.</summary>
    private static GameState OpenClaimWindow()
    {
        for (var seed = 1; seed < 500; seed++)
        {
            var state = Dealt(seed);
            if (state.Phase != GamePhase.AwaitingDiscard) continue;

            var hand = state.Hands[state.CurrentSeat].Concealed;

            foreach (var tile in hand.ToList())
            {
                var probe = MahjongGame.Deal(RuleOptions.Default, 1, 0, seed, Now).State;
                MahjongGame.Discard(probe, probe.CurrentSeat, tile.Id, Now);

                if (probe.Phase == GamePhase.AwaitingClaims) return probe;
            }
        }

        throw new InvalidOperationException("No deal in the first 500 seeds opened a claim window.");
    }

    [Fact]
    public void Turn_options_are_only_built_for_the_seat_whose_turn_it_is()
    {
        var state = Dealt();
        Assert.Equal(GamePhase.AwaitingDiscard, state.Phase);

        Assert.NotNull(GameViewBuilder.Build(state, "ABC123", state.CurrentSeat, Seats).YourTurn);

        foreach (var seat in Enumerable.Range(0, 4).Where(s => s != state.CurrentSeat))
            Assert.Null(GameViewBuilder.Build(state, "ABC123", seat, Seats).YourTurn);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Everything one seat is legitimately allowed to know the identity of: its own hand, every
    /// meld on the table, every exposed bonus tile, and everything already discarded.
    /// </summary>
    private static HashSet<int> AllowedTileIds(GameState state, int seat)
    {
        var allowed = new HashSet<int>();

        foreach (var tile in state.Hands[seat].Concealed) allowed.Add(tile.Id);

        foreach (var hand in state.Hands)
        {
            foreach (var meld in hand.Melds)
                foreach (var tile in meld.Tiles)
                    allowed.Add(tile.Id);

            foreach (var bonus in hand.Bonus) allowed.Add(bonus.Id);
        }

        foreach (var discard in state.Discards) allowed.Add(discard.Tile.Id);

        return allowed;
    }

    /// <summary>
    /// Every value that is a tile id in the serialised view. Tile ids only ever appear as the "id"
    /// property of a tile object, so walking for that name finds them all without picking up seat
    /// numbers, counts or balances.
    /// </summary>
    private static IEnumerable<int> TileIdsIn(string json)
    {
        using var document = JsonDocument.Parse(json);
        var found = new List<int>();
        Walk(document.RootElement, found);
        return found;

        static void Walk(JsonElement element, List<int> found)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.Number)
                            found.Add(property.Value.GetInt32());
                        else
                            Walk(property.Value, found);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item, found);
                    break;
            }
        }
    }
}
