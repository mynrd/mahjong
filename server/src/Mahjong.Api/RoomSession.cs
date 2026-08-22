using System.Collections.Concurrent;
using Mahjong.Domain;

namespace Mahjong.Api;

/// <summary>
/// The live state of one table, held in memory between database writes.
///
/// Every change to a hand goes through <see cref="RunAsync{T}"/>, which lets exactly one caller in
/// at a time. That serialisation is not optional: two players claiming the same discard arrive on
/// two different connections at the same instant, and the rules engine mutates shared state with
/// no locking of its own. Without a gate here, a discard could be melded twice, or the turn could
/// advance while a claim was being applied.
/// </summary>
public sealed class RoomSession(Guid roomId, string code)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Guid RoomId { get; } = roomId;
    public string Code { get; } = code;

    /// <summary>The hand in progress, or null when the room is in the lobby or between hands.</summary>
    public GameState? State { get; set; }

    /// <summary>Row id of the current hand, so actions and results can be attached to it.</summary>
    public Guid? GameId { get; set; }

    /// <summary>Next sequence number in the action log for this hand.</summary>
    public int NextSeq { get; set; } = 1;

    /// <summary>Live connections per seat. A seat can have more than one if a player opens two tabs.</summary>
    public ConcurrentDictionary<int, HashSet<string>> Connections { get; } = new();

    /// <summary>
    /// When the current claim window closes. The ticker uses this rather than a timer per window,
    /// so a window still expires correctly after a server restart mid-hand.
    /// </summary>
    public DateTimeOffset? ClaimDeadline => State?.Pending?.DeadlineUtc;

    /// <summary>When a bot is next allowed to move, so bots do not play instantly and unreadably.</summary>
    public DateTimeOffset BotNotBefore { get; set; } = DateTimeOffset.MinValue;

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<Task> action, CancellationToken cancel = default) =>
        RunAsync<object?>(async () => { await action(); return null; }, cancel);

    public void Attach(int seat, string connectionId) =>
        Connections.AddOrUpdate(
            seat,
            _ => [connectionId],
            (_, existing) =>
            {
                lock (existing) existing.Add(connectionId);
                return existing;
            });

    /// <summary>Removes a connection and reports whether the seat now has none left.</summary>
    public bool Detach(int seat, string connectionId)
    {
        if (!Connections.TryGetValue(seat, out var existing)) return true;

        lock (existing)
        {
            existing.Remove(connectionId);
            return existing.Count == 0;
        }
    }

    public IReadOnlyList<string> ConnectionsFor(int seat)
    {
        if (!Connections.TryGetValue(seat, out var existing)) return [];
        lock (existing) return existing.ToArray();
    }
}

/// <summary>
/// All live tables. Sessions are created on demand and kept for the life of the process, which is
/// fine for a game that runs on one machine on a LAN.
/// </summary>
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, RoomSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public RoomSession GetOrCreate(Guid roomId, string code) =>
        _sessions.GetOrAdd(code, key => new RoomSession(roomId, key));

    public RoomSession? Find(string code) =>
        _sessions.TryGetValue(code, out var session) ? session : null;

    public IReadOnlyList<RoomSession> Active => _sessions.Values.ToArray();
}
