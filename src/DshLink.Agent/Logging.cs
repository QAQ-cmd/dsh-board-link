using Microsoft.Extensions.Logging;

namespace DshLink;

/// <summary>Small size-rotating file logger so the agent can run without a console window.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly LogLevel _minimum;
    private StreamWriter? _writer;
    private long _written;

    public FileLoggerProvider(LoggingSection config)
    {
        _directory = config.Directory;
        _filePath = Path.Combine(_directory, "agent.log");
        _maxBytes = config.MaxFileBytes;
        _retainedFiles = config.RetainedFiles;
        _minimum = ParseLevel(config.Level);
        try
        {
            Directory.CreateDirectory(_directory);
            Open();
        }
        catch
        {
            // Logging must never prevent the agent from starting.
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimum;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{LevelText(level)}] {category}: {message}";
        if (exception is not null) line += Environment.NewLine + exception;
        lock (_gate)
        {
            try
            {
                if (_writer is null) return;
                if (_written + line.Length + 2 > _maxBytes) Rotate();
                _writer.WriteLine(line);
                _writer.Flush();
                _written += line.Length + Environment.NewLine.Length;
            }
            catch
            {
                // Ignore disk errors; console logging remains available.
            }
        }
    }

    private void Open()
    {
        var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _written = stream.Length;
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        try
        {
            for (var i = _retainedFiles - 1; i >= 1; i--)
            {
                var from = $"{_filePath}.{i}";
                var to = $"{_filePath}.{i + 1}";
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            if (File.Exists(_filePath)) File.Move(_filePath, _filePath + ".1", overwrite: true);
        }
        catch
        {
            // Best effort.
        }
        Open();
    }

    private static LogLevel ParseLevel(string value) => value switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "information" or "info" => LogLevel.Information,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        "none" => LogLevel.None,
        _ => LogLevel.Information,
    };

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
