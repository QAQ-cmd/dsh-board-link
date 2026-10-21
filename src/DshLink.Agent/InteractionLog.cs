using System.Collections.Concurrent;

namespace DshLink;

public sealed record InteractionEntry(
    DateTimeOffset At,
    string Direction,
    string Kind,
    string Tool,
    string Detail,
    int? Status,
    long DurationMs);

/// <summary>In-memory audit of DSH plugin -> board agent serial/ADB calls.</summary>
public sealed class InteractionLog
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<InteractionEntry> _entries = new();

    public void Add(InteractionEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity) _entries.TryDequeue(out _);
    }

    public IReadOnlyList<InteractionEntry> Snapshot(int limit)
    {
        limit = Math.Clamp(limit, 1, Capacity);
        return _entries.Reverse().Take(limit).Reverse().ToArray();
    }
}
