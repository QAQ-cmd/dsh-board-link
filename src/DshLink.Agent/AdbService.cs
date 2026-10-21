using System.Diagnostics;
using System.Text;

namespace DshLink;

public sealed class AdbService
{
    private readonly AdbSection _config;
    private readonly ILogger<AdbService> _logger;
    private readonly DeviceIOLog _ioLog;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private AdbSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAt;
    private string? _resolvedPath;

    public AdbService(AgentConfig config, ILogger<AdbService> logger, DeviceIOLog ioLog)
    {
        _config = config.Adb;
        _logger = logger;
        _ioLog = ioLog;
    }

    private static bool ShouldRecordIO(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(a => a.Contains("command -v getprop", StringComparison.Ordinal))) return false;
        return arguments.Any(a => a is "shell" or "logcat" or "push" or "pull" or "install" or "reboot" or "screencap" or "exec-out");
    }

    public string ResolvePath()
    {
        if (_resolvedPath is not null) return _resolvedPath;
        var configured = _config.Path;
        if (Path.IsPathRooted(configured) && File.Exists(configured))
            return _resolvedPath = configured;
        if (File.Exists(configured))
            return _resolvedPath = Path.GetFullPath(configured);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "adb.exe");
                if (File.Exists(candidate)) return _resolvedPath = candidate;
                candidate = Path.Combine(directory.Trim(), "adb");
                if (File.Exists(candidate)) return _resolvedPath = candidate;
            }
            catch
            {
                // A malformed PATH entry must not prevent the search.
            }
        }
        return _resolvedPath = configured;
    }

    public async Task<AdbSnapshot> GetSnapshotAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _cachedSnapshot is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(5))
            return _cachedSnapshot;

        await _snapshotGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _cachedSnapshot is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(5))
                return _cachedSnapshot;

            var snapshot = new AdbSnapshot { Path = ResolvePath(), CheckedAtUtc = DateTimeOffset.UtcNow };
            try
            {
                var version = await RunAsync(new[] { "version" }, 5000, 64 * 1024, cancellationToken);
                snapshot.Available = version.ExitCode == 0;
                snapshot.Version = FirstNonEmptyLine(version.Stdout) ?? FirstNonEmptyLine(version.Stderr);
                if (!snapshot.Available) snapshot.Error = Trim(version.Stderr) ?? $"adb exited with {version.ExitCode}";
                else
                {
                    var devices = await RunAsync(new[] { "devices", "-l" }, 8000, 256 * 1024, cancellationToken);
                    snapshot.Devices = ParseDevices(devices.Stdout);
                    if (devices.ExitCode != 0 && string.IsNullOrWhiteSpace(snapshot.Error))
                        snapshot.Error = Trim(devices.Stderr);
                    if (force)
                    {
                        _logger.LogInformation("adb devices ({Count}): {Devices}",
                            snapshot.Devices.Count,
                            string.Join(", ", snapshot.Devices.Select(d => $"{d.Serial}={d.State}")));
                    }
                }
            }
            catch (Exception ex)
            {
                snapshot.Available = false;
                snapshot.Error = ex.Message;
            }

            _cachedSnapshot = snapshot;
            _cachedAt = DateTimeOffset.UtcNow;
            return snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    public async Task<AdbDevice> EnsureDeviceAsync(string? serial, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(force: false, cancellationToken);
        if (!snapshot.Available)
            throw new InvalidOperationException($"adb is not available: {snapshot.Error ?? "unknown error"} (configure adb.path or add adb to PATH)");

        if (!string.IsNullOrWhiteSpace(serial))
        {
            var match = snapshot.Devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));
            if (match is null)
                throw new InvalidOperationException($"adb device '{serial}' not found; connected: {DescribeDevices(snapshot.Devices)}");
            if (match.State != "device")
                throw new InvalidOperationException($"adb device '{serial}' is {match.State}, not ready; run `adb devices` on the Windows host and authorize USB debugging");
            return match;
        }

        var ready = snapshot.Devices.Where(d => d.State == "device").ToArray();
        if (ready.Length == 1) return ready[0];
        if (ready.Length == 0)
            throw new InvalidOperationException($"no ready adb device; connected: {DescribeDevices(snapshot.Devices)}");
        throw new InvalidOperationException($"multiple ready adb devices ({DescribeDevices(ready)}); pass the serial argument");
    }

    public async Task<AdbResult> ShellAsync(string? serial, string command, int timeoutMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required");
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        return await RunAsync(new[] { "-s", device.Serial, "shell", command }, timeoutMs, _config.MaxOutputBytes, cancellationToken);
    }

    public async Task<AdbResult> LogcatAsync(string? serial, int lines, string? filter, string? platform,
        int timeoutMs, CancellationToken cancellationToken)
    {
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        lines = Math.Clamp(lines, 1, 5000);
        var selected = (platform ?? "auto").Trim().ToLowerInvariant();
        if (selected is not ("auto" or "android" or "linux"))
            throw new ArgumentException("platform must be auto, android or linux");

        if (selected == "auto")
        {
            var probe = await RunAsync(
                new[] { "-s", device.Serial, "shell", "command -v getprop >/dev/null 2>&1 && echo android || echo linux" },
                8000, 64 * 1024, cancellationToken);
            selected = probe.Stdout.Contains("android", StringComparison.OrdinalIgnoreCase) ? "android" : "linux";
        }

        AdbResult result;
        if (selected == "android")
        {
            var args = new List<string> { "-s", device.Serial, "logcat", "-d", "-t", lines.ToString(), "-v", "threadtime" };
            if (!string.IsNullOrWhiteSpace(filter))
            {
                foreach (var part in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)) args.Add(part);
            }
            result = await RunAsync(args, timeoutMs, _config.MaxOutputBytes, cancellationToken);
        }
        else
        {
            // Buildroot and most embedded Linux images do not have Android logcat. Read the
            // kernel/userspace log instead; /var/log/messages is the common Buildroot target.
            var command = $"tail -n {lines} /var/log/messages 2>/dev/null || dmesg | tail -n {lines}";
            if (!string.IsNullOrWhiteSpace(filter))
                command += $" | grep -F -- {ShellQuote(filter.Trim())}";
            result = await RunAsync(new[] { "-s", device.Serial, "shell", command },
                timeoutMs, _config.MaxOutputBytes, cancellationToken);
        }
        result.Platform = selected;
        return result;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public async Task<AdbBinaryResult> ScreenshotAsync(string? serial, CancellationToken cancellationToken)
    {
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        var result = await RunBinaryAsync(new[] { "-s", device.Serial, "exec-out", "screencap", "-p" },
            _config.DefaultTimeoutMs, _config.MaxTransferBytes, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"adb screencap failed ({result.ExitCode}): {result.Stderr}");
        return new AdbBinaryResult
        {
            Serial = device.Serial,
            DataBase64 = Convert.ToBase64String(result.Stdout),
            Bytes = result.Stdout.Length,
            Truncated = result.Truncated,
        };
    }

    public async Task<AdbResult> PushAsync(string? serial, string remotePath, byte[] data, CancellationToken cancellationToken)
    {
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        var temp = NewTempFile(".bin");
        try
        {
            await File.WriteAllBytesAsync(temp, data, cancellationToken);
            return await RunAsync(new[] { "-s", device.Serial, "push", temp, remotePath },
                Math.Max(_config.DefaultTimeoutMs, 60000), _config.MaxOutputBytes, cancellationToken);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public async Task<AdbBinaryResult> PullAsync(string? serial, string remotePath, long maxBytes,
        CancellationToken cancellationToken)
    {
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        var temp = NewTempFile(".bin");
        try
        {
            var result = await RunAsync(new[] { "-s", device.Serial, "pull", remotePath, temp },
                Math.Max(_config.DefaultTimeoutMs, 60000), _config.MaxOutputBytes, cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"adb pull failed ({result.ExitCode}): {result.Stderr}");
            var info = new FileInfo(temp);
            maxBytes = Math.Clamp(maxBytes, 1, _config.MaxTransferBytes);
            if (info.Length > maxBytes)
                throw new InvalidOperationException($"remote file is {info.Length} bytes, above the requested limit {maxBytes}; raise maxBytes");
            var bytes = await File.ReadAllBytesAsync(temp, cancellationToken);
            return new AdbBinaryResult
            {
                Serial = device.Serial,
                DataBase64 = Convert.ToBase64String(bytes),
                Bytes = bytes.Length,
                Truncated = false,
            };
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public async Task<AdbResult> InstallAsync(string? serial, byte[] apk, CancellationToken cancellationToken)
    {
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        var temp = NewTempFile(".apk");
        try
        {
            await File.WriteAllBytesAsync(temp, apk, cancellationToken);
            return await RunAsync(new[] { "-s", device.Serial, "install", "-r", temp },
                Math.Max(_config.DefaultTimeoutMs, 120000), _config.MaxOutputBytes, cancellationToken);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public async Task<AdbResult> RebootAsync(string? serial, string? mode, CancellationToken cancellationToken)
    {
        var device = await EnsureDeviceAsync(serial, cancellationToken);
        var args = new List<string> { "-s", device.Serial, "reboot" };
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (mode is not ("system" or "bootloader" or "recovery" or "sideload" or "fastboot"))
                throw new ArgumentException("mode must be system, bootloader, recovery, sideload or fastboot");
            if (mode != "system") args.Add(mode);
        }
        return await RunAsync(args, _config.DefaultTimeoutMs, _config.MaxOutputBytes, cancellationToken);
    }

    public async Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, int timeoutMs, int maxBytes,
        CancellationToken cancellationToken)
    {
        var binary = await RunProcessAsync(arguments, timeoutMs, maxBytes, cancellationToken);
        return new AdbResult
        {
            ExitCode = binary.ExitCode,
            Stdout = Encoding.UTF8.GetString(binary.Stdout),
            Stderr = binary.Stderr,
            Truncated = binary.Truncated,
            TimedOut = binary.TimedOut,
            DurationMs = binary.DurationMs,
        };
    }

    private async Task<AdbBinaryResult> RunBinaryAsync(IReadOnlyList<string> arguments, int timeoutMs,
        long maxBytes, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(arguments, timeoutMs, (int)Math.Min(maxBytes, int.MaxValue), cancellationToken);
        return new AdbBinaryResult
        {
            ExitCode = result.ExitCode,
            Stdout = result.Stdout,
            Stderr = result.Stderr,
            Truncated = result.Truncated,
            TimedOut = result.TimedOut,
            DurationMs = result.DurationMs,
        };
    }

    private async Task<ProcessResult> RunProcessAsync(IReadOnlyList<string> arguments, int timeoutMs,
        int maxBytes, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var started = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"failed to start adb at '{startInfo.FileName}'");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"failed to start adb at '{startInfo.FileName}': {ex.Message}", ex);
        }

        var commandLine = string.Join(' ', arguments);
        var recordIo = ShouldRecordIO(arguments);
        if (recordIo) _ioLog.Add("adb", "cmd", commandLine);
        _logger.LogInformation("adb start: {Command}", commandLine);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Clamp(timeoutMs, 1000, 600000));
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maxBytes, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, 256 * 1024, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        byte[] stdout;
        bool stdoutTruncated;
        string stderr;
        try
        {
            var outResult = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            stdout = outResult.Data;
            stdoutTruncated = outResult.Truncated;
            var errResult = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            stderr = Encoding.UTF8.GetString(errResult.Data);
            if (errResult.Truncated) stderr += "\n...[stderr truncated]";
        }
        catch
        {
            stdout = [];
            stdoutTruncated = false;
            stderr = "";
        }

        started.Stop();
        var exitCode = timedOut ? -1 : SafeExitCode(process);
        var stderrText = stderr.Trim();
        var stdoutPreview = SerialManager.TextPreview(Encoding.UTF8.GetString(stdout), 300);
        var stderrPreview = SerialManager.TextPreview(stderrText, 300);
        if (timedOut)
        {
            _logger.LogWarning("adb timeout: {Command} after {DurationMs}ms stdout={Stdout} stderr={Stderr}",
                commandLine, started.ElapsedMilliseconds, stdoutPreview, stderrPreview);
        }
        else if (exitCode != 0)
        {
            _logger.LogWarning("adb exit {ExitCode}: {Command} durationMs={DurationMs} stdout={Stdout} stderr={Stderr}",
                exitCode, commandLine, started.ElapsedMilliseconds, stdoutPreview, stderrPreview);
        }
        else
        {
            _logger.LogInformation("adb done: {Command} durationMs={DurationMs} stdout={Stdout} stderr={Stderr}",
                commandLine, started.ElapsedMilliseconds, stdoutPreview, stderrPreview);
        }

        if (recordIo)
        {
            var stdoutText = Encoding.UTF8.GetString(stdout);
            if (stdoutText.Length > 0) _ioLog.Add("adb", "out", stdoutText);
            if (stderrText.Length > 0) _ioLog.Add("adb", "err", stderrText);
        }
        return new ProcessResult
        {
            ExitCode = exitCode,
            Stdout = stdout,
            Stderr = stderrText,
            Truncated = stdoutTruncated,
            TimedOut = timedOut,
            DurationMs = (int)started.ElapsedMilliseconds,
        };
    }

    private static async Task<BoundedRead> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0) break;
            var remaining = maxBytes - (int)memory.Length;
            if (remaining > 0) memory.Write(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return new BoundedRead(memory.ToArray(), truncated);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    public static List<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("*", StringComparison.Ordinal)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var device = new AdbDevice { Serial = parts[0], State = parts[1] };
            foreach (var part in parts.Skip(2))
            {
                var separator = part.IndexOf(':');
                if (separator <= 0) continue;
                var key = part[..separator];
                var value = part[(separator + 1)..];
                switch (key)
                {
                    case "model": device.Model = value; break;
                    case "product": device.Product = value; break;
                    case "device": device.Device = value; break;
                    case "transport_id": device.TransportId = value; break;
                }
            }
            devices.Add(device);
        }
        return devices;
    }

    private static string DescribeDevices(IEnumerable<AdbDevice> devices)
    {
        var text = string.Join(", ", devices.Select(d => $"{d.Serial} ({d.State})"));
        return string.IsNullOrEmpty(text) ? "none" : text;
    }

    private static string? FirstNonEmptyLine(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string? Trim(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NewTempFile(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dsh-link");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private readonly record struct BoundedRead(byte[] Data, bool Truncated);

    private sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public byte[] Stdout { get; set; } = [];
        public string Stderr { get; set; } = "";
        public bool Truncated { get; set; }
        public bool TimedOut { get; set; }
        public int DurationMs { get; set; }
    }
}

public sealed class AdbResult
{
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public bool Truncated { get; set; }
    public bool TimedOut { get; set; }
    public int DurationMs { get; set; }
    public string? Platform { get; set; }
    public bool Ok => ExitCode == 0 && !TimedOut;
}

public sealed class AdbBinaryResult
{
    public int ExitCode { get; set; }
    public string Serial { get; set; } = "";
    public string DataBase64 { get; set; } = "";
    public long Bytes { get; set; }
    public bool Truncated { get; set; }
    public bool TimedOut { get; set; }
    public int DurationMs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Stdout { get; set; } = [];
    public string Stderr { get; set; } = "";
}

public sealed class AdbDevice
{
    public string Serial { get; set; } = "";
    public string State { get; set; } = "";
    public string? Model { get; set; }
    public string? Product { get; set; }
    public string? Device { get; set; }
    public string? TransportId { get; set; }
}

public sealed class AdbSnapshot
{
    public bool Available { get; set; }
    public string Path { get; set; } = "";
    public string? Version { get; set; }
    public string? Error { get; set; }
    public List<AdbDevice> Devices { get; set; } = new();
    public DateTimeOffset CheckedAtUtc { get; set; }
}
