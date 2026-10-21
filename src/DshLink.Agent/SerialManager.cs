using System.Text;

namespace DshLink;

public sealed class SerialManager : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SerialSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SerialSection _config;
    private readonly ILogger<SerialManager> _logger;
    private readonly DeviceIOLog _ioLog;
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    public SerialManager(AgentConfig config, ILogger<SerialManager> logger, DeviceIOLog ioLog)
    {
        _config = config.Serial;
        _logger = logger;
        _ioLog = ioLog;
    }

    internal static string TextPreview(string text, int max = 160)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, max + 8));
        foreach (var ch in text)
        {
            if (builder.Length >= max)
            {
                builder.Append('…');
                break;
            }
            builder.Append(ch switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(ch) => $"\\x{(int)ch:X2}",
                _ => ch.ToString(),
            });
        }
        return builder.ToString();
    }

    public IReadOnlyList<SerialPortInfo> GetPorts() => WindowsSerialPort.ListPorts();

    public SerialSession Open(SerialOpenOptions options)
    {
        var portName = WindowsSerialPort.NormalizePortName(options.Port);
        lock (_gate)
        {
            if (_sessions.Count >= _config.MaxSessions)
                throw new InvalidOperationException($"serial session limit reached ({_config.MaxSessions}); close a session first");
            if (_sessions.Values.Any(s => string.Equals(s.PortName, portName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"serial port {portName} is already open in this agent");

            var serial = new WindowsSerialPort(portName, options.BaudRate, options.DataBits, options.Parity,
                options.StopBits, options.Handshake, options.Dtr, options.Rts);
            serial.Open();
            var id = $"ser-{_instanceId}-{Guid.NewGuid():N}"[..24];
            var session = new SerialSession(id, serial, _config.BufferBytes, _logger, _ioLog);
            _sessions[id] = session;
            _ioLog.Add("serial", "info", $"OPEN {portName} @ {options.BaudRate} data={options.DataBits} parity={options.Parity} stop={options.StopBits} handshake={options.Handshake} dtr={options.Dtr} rts={options.Rts}");
            _logger.LogInformation(
                "serial opened: {SessionId} {Port} @ {Baud} {DataBits}{Parity}{StopBits} handshake={Handshake} dtr={Dtr} rts={Rts}",
                id, portName, options.BaudRate, options.DataBits, options.Parity, options.StopBits,
                options.Handshake, options.Dtr, options.Rts);
            return session;
        }
    }

    public SerialSession Get(string sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session)) return session;
        }
        throw new KeyNotFoundException($"serial session '{sessionId}' does not exist (it may have been closed or the agent restarted)");
    }

    public bool Close(string sessionId)
    {
        SerialSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out session)) return false;
        }
        session.Dispose();
        _ioLog.Add("serial", "info", $"CLOSE {session.PortName} ({sessionId})");
        _logger.LogInformation("serial closed: {SessionId} {Port}", sessionId, session.PortName);
        return true;
    }

    public IReadOnlyList<SerialSessionSnapshot> ListSessions()
    {
        SerialSession[] sessions;
        lock (_gate) sessions = _sessions.Values.ToArray();
        return sessions.Select(s => s.Snapshot()).ToArray();
    }

    public async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        if (_config.IdleTimeoutSeconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                List<SerialSession> expired;
                lock (_gate)
                {
                    expired = _sessions.Values
                        .Where(s => DateTimeOffset.UtcNow - s.LastActivityUtc > TimeSpan.FromSeconds(_config.IdleTimeoutSeconds))
                        .ToList();
                }
                foreach (var session in expired)
                {
                    _logger.LogInformation("serial idle timeout: {SessionId} {Port}", session.Id, session.PortName);
                    Close(session.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public void Dispose()
    {
        List<SerialSession> sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var session in sessions) session.Dispose();
    }
}

public sealed class SerialSession : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly WindowsSerialPort _port;
    private readonly ILogger _logger;
    private readonly DeviceIOLog _ioLog;
    private int _head;
    private int _count;
    private long _totalIn;
    private long _dropped;
    private string? _lastError;
    private volatile bool _faulted;
    private volatile bool _disposed;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;

    public string Id { get; }
    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public int DataBits => _port.DataBits;
    public string Parity => _port.Parity;
    public string StopBits => _port.StopBits;
    public string Handshake => _port.Handshake;
    public DateTimeOffset OpenedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityUtc => _lastActivityUtc;
    public string? LastError => _lastError;
    public bool Faulted => _faulted;
    public long TotalBytesWritten => _port.BytesWritten;

    public SerialSession(string id, WindowsSerialPort port, int bufferBytes, ILogger logger, DeviceIOLog ioLog)
    {
        Id = id;
        _port = port;
        _buffer = new byte[bufferBytes];
        _logger = logger;
        _ioLog = ioLog;
        _port.StartReading(Append, OnFault);
    }

    private void Append(byte[] data, int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (count > _buffer.Length)
            {
                var skip = count - _buffer.Length;
                _dropped += skip;
                data = data.AsSpan(skip, _buffer.Length).ToArray();
                count = _buffer.Length;
            }

            var free = _buffer.Length - _count;
            if (count > free)
            {
                var drop = count - free;
                _head = (_head + drop) % _buffer.Length;
                _count -= drop;
                _dropped += drop;
            }

            var tail = (_head + _count) % _buffer.Length;
            var first = Math.Min(count, _buffer.Length - tail);
            Buffer.BlockCopy(data, 0, _buffer, tail, first);
            if (count > first) Buffer.BlockCopy(data, first, _buffer, 0, count - first);
            _count += count;
            _totalIn += count;
            _ioLog.Add("serial", "rx", Encoding.UTF8.GetString(data, 0, count));
        }
        _signal.Release();
    }

    private void OnFault(Exception ex)
    {
        lock (_gate)
        {
            _lastError = ex.Message;
            _faulted = true;
        }
        _ioLog.Add("serial", "err", $"FAULT {PortName}: {ex.Message}");
        _logger.LogWarning(ex, "serial port fault: {SessionId} {Port}", Id, PortName);
        try { _signal.Release(); } catch { /* ignore */ }
    }

    public async Task<SerialReadResult> ReadAsync(int timeoutMs, int maxBytes, bool peek, CancellationToken cancellationToken)
    {
        maxBytes = Math.Clamp(maxBytes, 1, _buffer.Length);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Clamp(timeoutMs, 0, 60000));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SerialReadResult? result = null;
            lock (_gate)
            {
                if (_count > 0)
                {
                    var take = Math.Min(maxBytes, _count);
                    var bytes = new byte[take];
                    var first = Math.Min(take, _buffer.Length - _head);
                    Buffer.BlockCopy(_buffer, _head, bytes, 0, first);
                    if (take > first) Buffer.BlockCopy(_buffer, 0, bytes, first, take - first);
                    var truncated = take < _count;
                    if (!peek)
                    {
                        _head = (_head + take) % _buffer.Length;
                        _count -= take;
                    }
                    _lastActivityUtc = DateTimeOffset.UtcNow;
                    result = new SerialReadResult
                    {
                        SessionId = Id,
                        Port = PortName,
                        Text = Encoding.UTF8.GetString(bytes),
                        Base64 = Convert.ToBase64String(bytes),
                        Bytes = take,
                        TotalBytesIn = _totalIn,
                        DroppedBytes = _dropped,
                        BufferedBytes = _count,
                        Truncated = truncated,
                        Faulted = _faulted,
                        Error = _lastError,
                        Modem = TryModemStatus(),
                    };
                }
            }

            if (result is not null)
            {
                if (result.Bytes > 0)
                {
                    _logger.LogInformation(
                        "serial rx: {SessionId} {Port} bytes={Bytes} total={Total} preview={Preview}",
                        Id, PortName, result.Bytes, result.TotalBytesIn, SerialManager.TextPreview(result.Text));
                }
                if (!peek && result.BufferedBytes > 0) _signal.Release();
                return result;
            }

            if (_faulted)
            {
                var error = _lastError ?? "serial reader stopped";
                throw new IOException($"serial session {Id} ({PortName}) is faulted: {error}");
            }

            var remaining = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                return new SerialReadResult
                {
                    SessionId = Id,
                    Port = PortName,
                    Text = "",
                    Base64 = "",
                    Bytes = 0,
                    TotalBytesIn = _totalIn,
                    DroppedBytes = _dropped,
                    BufferedBytes = _count,
                    Truncated = false,
                    Faulted = _faulted,
                    Error = _lastError,
                    Modem = TryModemStatus(),
                };
            }

            try
            {
                await _signal.WaitAsync(Math.Min(remaining, 250), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    public int Write(byte[] data)
    {
        if (data.Length == 0) return 0;
        var written = _port.Write(data);
        _lastActivityUtc = DateTimeOffset.UtcNow;
        var text = Encoding.UTF8.GetString(data);
        _logger.LogInformation("serial tx: {SessionId} {Port} bytes={Bytes} preview={Preview}",
            Id, PortName, written, SerialManager.TextPreview(text));
        _ioLog.Add("serial", "tx", text);
        return written;
    }

    public async Task PulseAsync(string line, int durationMs, CancellationToken cancellationToken)
    {
        durationMs = Math.Clamp(durationMs, 10, 5000);
        var isDtr = line.Equals("dtr", StringComparison.OrdinalIgnoreCase);
        var isRts = line.Equals("rts", StringComparison.OrdinalIgnoreCase);
        if (!isDtr && !isRts) throw new ArgumentException("line must be 'dtr' or 'rts'");
        if (isDtr) _port.SetDtr(false); else _port.SetRts(false);
        try
        {
            await Task.Delay(durationMs, cancellationToken);
        }
        finally
        {
            if (isDtr) _port.SetDtr(true); else _port.SetRts(true);
        }
        _lastActivityUtc = DateTimeOffset.UtcNow;
        _ioLog.Add("serial", "info", $"PULSE {PortName} {line.ToUpperInvariant()} {durationMs}ms");
        _logger.LogInformation("serial pulse: {SessionId} {Port} line={Line} durationMs={DurationMs}",
            Id, PortName, line, durationMs);
    }

    public SerialSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SerialSessionSnapshot
            {
                SessionId = Id,
                Port = PortName,
                BaudRate = BaudRate,
                DataBits = DataBits,
                Parity = Parity,
                StopBits = StopBits,
                Handshake = Handshake,
                OpenedAtUtc = OpenedAtUtc,
                LastActivityUtc = _lastActivityUtc,
                TotalBytesIn = _totalIn,
                DroppedBytes = _dropped,
                BufferedBytes = _count,
                Faulted = _faulted,
                Error = _lastError,
                Modem = TryModemStatus(),
            };
        }
    }

    private SerialModemStatus? TryModemStatus()
    {
        try { return _port.GetModemStatus(); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _port.Dispose(); } catch { /* ignore */ }
        // Keep _signal alive: a concurrent ReadAsync may be waiting on it. It is a small
        // managed object and is collected with the session.
    }
}

public sealed class SerialOpenOptions
{
    public string Port { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "none";
    public string StopBits { get; set; } = "one";
    public string Handshake { get; set; } = "none";
    public bool Dtr { get; set; } = true;
    public bool Rts { get; set; } = true;
}

public sealed class SerialReadResult
{
    public string SessionId { get; set; } = "";
    public string Port { get; set; } = "";
    public string Text { get; set; } = "";
    public string Base64 { get; set; } = "";
    public int Bytes { get; set; }
    public long TotalBytesIn { get; set; }
    public long DroppedBytes { get; set; }
    public int BufferedBytes { get; set; }
    public bool Truncated { get; set; }
    public bool Faulted { get; set; }
    public string? Error { get; set; }
    public SerialModemStatus? Modem { get; set; }
}

public sealed class SerialSessionSnapshot
{
    public string SessionId { get; set; } = "";
    public string Port { get; set; } = "";
    public int BaudRate { get; set; }
    public int DataBits { get; set; }
    public string Parity { get; set; } = "";
    public string StopBits { get; set; } = "";
    public string Handshake { get; set; } = "";
    public DateTimeOffset OpenedAtUtc { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }
    public long TotalBytesIn { get; set; }
    public long DroppedBytes { get; set; }
    public int BufferedBytes { get; set; }
    public bool Faulted { get; set; }
    public string? Error { get; set; }
    public SerialModemStatus? Modem { get; set; }
}
