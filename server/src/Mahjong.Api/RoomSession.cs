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
    /// When the call standing on the open discard takes the tile, or null while nothing has been
    /// called on it and so nothing is due. The ticker reads this off the state rather than keeping
    /// a timer per window, so a call still resolves after a server restart mid-hand.
    /// </summary>
    public DateTimeOffset? ClaimDeadline => State?.Pending?.DeadlineUtc;

    /// <summary>When a bot is next allowed to move, so bots do not play instantly and unreadably.</summary>
    public DateTimeOffset BotNotBefore { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Seats that have turned their hand face up for the hand just finished.
    ///
    /// Held here rather than on <see cref="GameState"/> because it is not a fact about the rules:
    /// nothing in the engine reads it, and a hand plays out identically whether every seat showed
    /// or none did. It is per hand and cleared on the next deal, so a player who showed once is not
    /// showing for the rest of the evening.
    ///
    /// Replaced whole rather than mutated. Reads happen outside the session gate - a client that
    /// has just connected builds its view without taking it - and swapping the reference means a
    /// reader either sees the old set or the new one, never a half-written one.
    /// </summary>
    public IReadOnlySet<int> RevealedSeats { get; private set; } = new HashSet<int>();

    public void Reveal(int seat) => RevealedSeats = new HashSet<int>(RevealedSeats) { seat };

    public void ClearReveals() => RevealedSeats = new HashSet<int>();

    /// <summary>
    /// The standing offer of another game, or null when nobody has called one.
    ///
    /// Here for the same reasons as the reveals above: it is not a fact about the rules, it lasts
    /// only as long as the gap between two hands, and it is read outside the session gate while
    /// views are being built - so it is swapped whole rather than edited in place.
    /// </summary>
    public NewGameProposal? Proposal { get; private set; }

    public void Propose(NewGameProposal proposal) => Proposal = proposal;

    public void Accept(int seat) => Proposal = Proposal?.With(seat);

    /// <summary>Drops a seat's answer, for a player who has left or been removed.</summary>
    public void Vacate(int seat) => Proposal = Proposal?.Without(seat);

    public void ClearProposal() => Proposal = null;

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
