using Mahjong.Infrastructure;

namespace Mahjong.Api;

/// <summary>
/// Who is sitting where. Small on purpose: it exists so that the two places that seat bots - the
/// lobby before the first hand, and the table when a seat is left empty between hands - do it the
/// same way, rather than growing two spellings of "find the free seats and put a bot in each".
/// </summary>
public static class Roster
{
    public const int Seats = 4;

    /// <summary>
    /// Puts a bot in every free seat, or in <paramref name="count"/> of them, and returns the ones
    /// it seated. Adds to the context and to <paramref name="room"/>; saving is the caller's, so a
    /// caller that has more to write in the same unit of work still writes it once.
    /// </summary>
    public static IReadOnlyList<Player> SeatBots(MahjongDbContext db, Room room, int? count = null)
    {
        var taken = room.Players.Select(p => p.Seat).ToHashSet();
        var free = Enumerable.Range(0, Seats).Where(s => !taken.Contains(s)).ToList();

        var wanted = Math.Min(count ?? free.Count, free.Count);
        if (wanted <= 0) return [];

        // Numbered by how many bots the room has had rather than by seat, so a table with one bot
        // in it calls that bot "Bot 1" wherever it happens to be sitting.
        var botNumber = room.Players.Count(p => p.IsBot);
        var seated = new List<Player>(wanted);

        foreach (var seat in free.Take(wanted))
        {
            botNumber++;

            var bot = new Player
            {
                RoomId = room.Id,
                DisplayName = $"Bot {botNumber}",
                Seat = seat,
                IsBot = true,
                IsConnected = true,
                // Bots never authenticate, but the column is not nullable and a shared empty value
                // would collide on the token index, so they get a throwaway token like anyone else.
                TokenHash = PlayerToken.HashOf(PlayerToken.Issue()),
            };

            db.Players.Add(bot);

            // Tracking the new row already puts it on the room's collection, because the room is
            // tracked too and EF fixes navigations up as it goes. Adding it again would leave the
            // same player in the list twice, and everything downstream that keys players by seat -
            // the lobby view, the action log - falls over on the duplicate. This is here only for
            // the case where that fixup has not happened.
            if (!room.Players.Contains(bot)) room.Players.Add(bot);

            seated.Add(bot);
        }

        return seated;
    }
}
