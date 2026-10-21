namespace DshLink;

/// <summary>Thread-safe character ring buffer that keeps the newest output for polling jobs.</summary>
public sealed class TextRingBuffer
{
    private readonly object _gate = new();
    private readonly char[] _buffer;
    private int _start;
    private int _count;
    private long _total;

    public TextRingBuffer(int capacity)
    {
        if (capacity < 256) capacity = 256;
        _buffer = new char[capacity];
    }

    public long TotalChars
    {
        get { lock (_gate) return _total; }
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            _total += text.Length;
            if (text.Length >= _buffer.Length)
            {
                var offset = text.Length - _buffer.Length;
                text.AsSpan(offset).CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                return;
            }

            var free = _buffer.Length - _count;
            if (text.Length > free)
            {
                var drop = text.Length - free;
                _start = (_start + drop) % _buffer.Length;
                _count -= drop;
            }
            var tail = (_start + _count) % _buffer.Length;
            var first = Math.Min(text.Length, _buffer.Length - tail);
            text.AsSpan(0, first).CopyTo(_buffer.AsSpan(tail));
            if (text.Length > first) text.AsSpan(first).CopyTo(_buffer);
            _count += text.Length;
        }
    }

    /// <summary>Return the newest at most maxChars characters, plus whether older data was dropped.</summary>
    public (string Text, bool Truncated) Tail(int maxChars)
    {
        if (maxChars < 1) maxChars = 1;
        lock (_gate)
        {
            var take = Math.Min(maxChars, _count);
            var start = (_start + _count - take) % _buffer.Length;
            var chars = new char[take];
            var first = Math.Min(take, _buffer.Length - start);
            Array.Copy(_buffer, start, chars, 0, first);
            if (take > first) Array.Copy(_buffer, 0, chars, first, take - first);
            return (new string(chars), _total > take);
        }
    }
}
