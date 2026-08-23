using Mahjong.Domain;
using Mahjong.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Api;

/// <summary>
/// The clock of the game. Two things have to happen without any player asking for them: a claim
/// window has to close when its time runs out, and a bot has to take its turn.
///
/// Both are done from one loop rather than from per-window timers. A timer scheduled when a
/// discard happens is lost if the process restarts, leaving a hand wedged open forever; a loop
/// that reads the deadline off the state picks it up again on the next tick.
/// </summary>
public sealed class GameTicker(
    IServiceScopeFactory scopes,
    RoomRegistry registry,
    TimeProvider clock,
    ILogger<GameTicker> logger) : BackgroundService
{
    /// <summary>How often the loop runs. Fine-grained enough for a six-second claim window.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A bot waits this long before moving. Instant bot play is unreadable: tiles would appear and
    /// vanish between frames and a human could not follow whose turn it was.
    /// </summary>
    private static readonly TimeSpan BotThinkingTime = TimeSpan.FromMilliseconds(900);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stopping.IsCancellationRequested && await timer.WaitForNextTickAsync(stopping))
        {
            foreach (var session in registry.Active)
            {
                try
                {
                    await TickAsync(session, stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One wedged table must not stop the others from being ticked.
                    logger.LogError(ex, "Ticking room {Code} failed.", session.Code);
                }
            }
        }
    }

    private async Task TickAsync(RoomSession session, CancellationToken cancel)
    {
        if (session.State is not { } state || state.Phase == GamePhase.HandOver) return;

        var now = clock.GetUtcNow();

        if (session.ClaimDeadline is { } deadline && deadline <= now)
        {
            using var scope = scopes.CreateScope();
            var games = scope.ServiceProvider.GetRequiredService<GameService>();
            await games.ExpireClaimsAsync(session, cancel);
            return;
        }

        await TickBotsAsync(session, now, cancel);
    }

    private async Task TickBotsAsync(RoomSession session, DateTimeOffset now, CancellationToken cancel)
    {
        if (session.State is not { } state) return;
        if (now < session.BotNotBefore) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MahjongDbContext>();

        var botSeats = await db.Players
            .Where(p => p.RoomId == session.RoomId && p.IsBot)
            .Select(p => p.Seat)
            .ToListAsync(cancel);

        if (botSeats.Count == 0) return;

        var games = scope.ServiceProvider.GetRequiredService<GameService>();

        foreach (var seat in botSeats)
        {
            var move = SimpleBot.Decide(state, seat, now);
            if (move is null) continue;

            var result = await games.MoveAsync(session.Code, seat, move, cancel);

            if (!result.Success)
                logger.LogDebug("Bot on seat {Seat} in {Code} could not {Move}: {Error}",
                    seat, session.Code, move.GetType().Name, result.Error);

            // One bot action per tick, so a table of three bots plays at a watchable pace instead
            // of resolving a whole hand inside a single tick.
            session.BotNotBefore = now + BotThinkingTime;
            return;
        }
    }
}
