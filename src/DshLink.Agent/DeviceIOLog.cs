using System.Collections.Concurrent;

namespace DshLink;

public sealed record DeviceIOEntry(DateTimeOffset At, string Kind, string Direction, string Text);

/// <summary>Raw serial RX/TX and ADB command output, kept for the console log tab.</summary>
public sealed class DeviceIOLog
{
    private const int MaxEntries = 2000;
    private const int MaxText = 64 * 1024;
    private const long MaxTotalChars = 4_000_000;
    private readonly ConcurrentQueue<DeviceIOEntry> _entries = new();
    private long _totalChars;

    public void Add(string kind, string direction, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxText)
            text = text[..MaxText] + $"\n...[truncated {text.Length - MaxText} chars]";
        _entries.Enqueue(new DeviceIOEntry(DateTimeOffset.UtcNow, kind, direction, text));
        Interlocked.Add(ref _totalChars, text.Length);
        while (_entries.Count > MaxEntries || Interlocked.Read(ref _totalChars) > MaxTotalChars)
        {
            if (!_entries.TryDequeue(out var old)) break;
            Interlocked.Add(ref _totalChars, -old.Text.Length);
        }
    }

    public IReadOnlyList<DeviceIOEntry> Snapshot(int limit)
    {
        limit = Math.Clamp(limit, 1, MaxEntries);
        return _entries.Reverse().Take(limit).Reverse().ToArray();
    }

    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _totalChars, 0);
    }
}
