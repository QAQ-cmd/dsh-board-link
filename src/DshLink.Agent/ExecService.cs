using System.Diagnostics;
using System.Text;

namespace DshLink;

public sealed class ExecService : IDisposable
{
    private readonly ExecSection _config;
    private readonly ILogger<ExecService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ProcessJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _running;

    public ExecService(AgentConfig config, ILogger<ExecService> logger)
    {
        _config = config.Exec;
        _logger = logger;
        _ = CleanupLoopAsync(_disposeCts.Token);
    }

    public ExecSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ExecSnapshot
            {
                Enabled = _config.Enabled,
                AllowAny = _config.AllowAny,
                AllowShell = _config.AllowShell,
                AllowedExecutables = _config.AllowedExecutables.ToList(),
                AllowedWorkingRoots = _config.AllowedWorkingRoots.ToList(),
                RunningJobs = _running,
                RetainedJobs = _jobs.Count,
                MaxJobs = _config.MaxJobs,
            };
        }
    }

    public async Task<ExecResult> RunAsync(ExecRequest request, CancellationToken cancellationToken)
    {
        request.CloseStdin ??= true;
        var job = await StartJobAsync(request, cancellationToken);
        try
        {
            await job.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            job.Kill();
            throw;
        }
        return job.ToExecResult();
    }

    public async Task<ProcessJob> StartJobAsync(ExecRequest request, CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
            throw new ExecDisabledException("exec is disabled on this Windows agent; set exec.enabled=true in config.json and restart dsh-link");
        if (request is null) throw new ArgumentException("request body is required");

        var startInfo = BuildStartInfo(request, out var mode, out var commandDisplay, out var cwd, out var timeoutMs);
        ProcessJob job;
        lock (_gate)
        {
            if (_running >= _config.MaxJobs)
                throw new InvalidOperationException($"job limit reached ({_config.MaxJobs} running); wait for a job to finish or kill one");
            _running++;
            job = new ProcessJob($"job-{Guid.NewGuid():N}"[..16], mode, commandDisplay, cwd, timeoutMs,
                _config.MaxOutputBytes, _logger);
            _jobs[job.Id] = job;
        }

        try
        {
            await job.StartAsync(startInfo, request.Stdin, request.CloseStdin ?? false, cancellationToken);
        }
        catch
        {
            lock (_gate)
            {
                _jobs.Remove(job.Id);
                _running--;
            }
            job.Dispose();
            throw;
        }

        _ = job.Completion.ContinueWith(_ =>
        {
            lock (_gate) _running = Math.Max(0, _running - 1);
        }, TaskScheduler.Default);
        return job;
    }

    public ProcessJob GetJob(string jobId)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var job)) return job;
        }
        throw new KeyNotFoundException($"job '{jobId}' does not exist or was already cleaned up");
    }

    public IReadOnlyList<JobSnapshot> ListJobs(int tailChars = 0)
    {
        ProcessJob[] jobs;
        lock (_gate) jobs = _jobs.Values.ToArray();
        return jobs.OrderByDescending(j => j.StartedAtUtc).Select(j => j.Snapshot(tailChars)).ToArray();
    }

    public JobSnapshot KillJob(string jobId)
    {
        var job = GetJob(jobId);
        job.Kill();
        return job.Snapshot(8192);
    }

    public async Task<JobSnapshot> WriteJobInputAsync(string jobId, string text, bool close, CancellationToken cancellationToken)
    {
        var job = GetJob(jobId);
        await job.WriteInputAsync(text, close, cancellationToken);
        return job.Snapshot(8192);
    }

    public async Task<WindowsFileWriteResult> WriteFileAsync(string path, byte[] data, bool overwrite,
        bool createDirectories, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (data.LongLength > _config.MaxFileBytes)
            throw new ArgumentException($"file is {data.LongLength} bytes, above exec.maxFileBytes={_config.MaxFileBytes}");
        var full = ValidateFilePath(path);
        if (Directory.Exists(full)) throw new ArgumentException($"path is a directory: {full}");
        if (File.Exists(full) && !overwrite)
            throw new InvalidOperationException($"file already exists and overwrite=false: {full}");
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent)) throw new ArgumentException($"invalid file path: {full}");
        if (!Directory.Exists(parent))
        {
            if (!createDirectories) throw new ArgumentException($"parent directory does not exist: {parent}");
            Directory.CreateDirectory(parent);
        }
        var temp = Path.Combine(parent, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, data, cancellationToken);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
        _logger.LogInformation("file written: {Path} ({Bytes} bytes)", full, data.Length);
        return new WindowsFileWriteResult { Path = full, Bytes = data.Length };
    }

    public async Task<WindowsFileReadResult> ReadFileAsync(string path, long maxBytes,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var full = ValidateFilePath(path);
        if (!File.Exists(full)) throw new KeyNotFoundException($"file not found: {full}");
        var info = new FileInfo(full);
        maxBytes = Math.Clamp(maxBytes, 1, _config.MaxFileBytes);
        if (info.Length > maxBytes)
            throw new ArgumentException($"file is {info.Length} bytes, above the requested maxBytes={maxBytes}");
        var data = await File.ReadAllBytesAsync(full, cancellationToken);
        return new WindowsFileReadResult { Path = full, Bytes = data.Length, Data = data };
    }

    public IReadOnlyList<WindowsDirEntry> ListDirectory(string path)
    {
        EnsureEnabled();
        var full = ValidateFilePath(path);
        if (!Directory.Exists(full)) throw new ArgumentException($"directory not found: {full}");
        return new DirectoryInfo(full).EnumerateFileSystemInfos()
            .OrderBy(entry => entry is DirectoryInfo ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new WindowsDirEntry
            {
                Name = entry.Name,
                Path = entry.FullName,
                IsDirectory = entry is DirectoryInfo,
                Length = entry is FileInfo file ? file.Length : 0,
                LastWriteUtc = entry.LastWriteTimeUtc,
            })
            .ToArray();
    }

    private void EnsureEnabled()
    {
        if (!_config.Enabled)
            throw new ExecDisabledException("exec is disabled on this Windows agent; set exec.enabled=true in config.json and restart dsh-link");
    }

    private string ValidateFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { throw new ArgumentException($"invalid path: {ex.Message}"); }
        if (_config.AllowedWorkingRoots.Count > 0)
        {
            var allowed = _config.AllowedWorkingRoots.Any(root =>
                string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (!allowed)
                throw new ExecSecurityException($"path is outside exec.allowedWorkingRoots: {full}");
        }
        return full;
    }

    private ProcessStartInfo BuildStartInfo(ExecRequest request, out string mode, out string commandDisplay,
        out string? cwd, out int timeoutMs)
    {
        mode = (request.Mode ?? "cmd").Trim().ToLowerInvariant();
        if (mode is not ("cmd" or "powershell" or "direct"))
            throw new ArgumentException("mode must be cmd, powershell or direct");
        if (string.IsNullOrWhiteSpace(request.Command))
            throw new ArgumentException("command is required");
        if (request.Command.Length > 32 * 1024)
            throw new ArgumentException("command is too long (32 KiB max)");

        cwd = ValidateWorkingDirectory(request.Cwd);
        timeoutMs = Math.Clamp(request.TimeoutMs ?? _config.DefaultTimeoutMs, 1000, _config.MaxTimeoutMs);

        string executable;
        var args = new List<string>();
        if (mode == "cmd")
        {
            if (!_config.AllowShell)
                throw new ExecSecurityException("shell execution is disabled; set exec.allowShell=true to allow cmd/powershell");
            executable = ResolveExecutable(_config.CmdPath);
            ValidateAllowedExecutable(executable, mode);
            args.Add("/d");
            args.Add("/s");
            args.Add("/c");
            args.Add(request.Command);
            commandDisplay = $"cmd.exe /d /s /c {request.Command}";
        }
        else if (mode == "powershell")
        {
            if (!_config.AllowShell)
                throw new ExecSecurityException("shell execution is disabled; set exec.allowShell=true to allow cmd/powershell");
            executable = ResolveExecutable(_config.PowerShellPath);
            ValidateAllowedExecutable(executable, mode);
            args.Add("-NoLogo");
            args.Add("-NoProfile");
            args.Add("-NonInteractive");
            args.Add("-Command");
            var requestedEncoding = (request.Encoding ?? "auto").Trim().ToLowerInvariant();
            var command = request.Command;
            if (requestedEncoding is "auto" or "utf8" or "utf-8")
            {
                // Windows PowerShell inherits an OEM console code page when stderr/stdout are pipes.
                // Force UTF-8 so a model sees non-ASCII output correctly; a caller can opt out by
                // explicitly requesting encoding=oem/utf16.
                command = "[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Console]::OutputEncoding; " + command;
            }
            args.Add(command);
            commandDisplay = $"powershell -NoLogo -NoProfile -NonInteractive -Command {request.Command}";
        }
        else
        {
            var extension = Path.GetExtension(request.Command);
            if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("use mode=cmd to run .bat/.cmd scripts, or pass the interpreter explicitly");
            executable = ResolveExecutable(request.Command);
            var resolvedExtension = Path.GetExtension(executable);
            if (resolvedExtension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                resolvedExtension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("resolved a .bat/.cmd script; use mode=cmd to run it through cmd.exe");
            ValidateAllowedExecutable(executable, mode);
            if (request.Args is not null)
            {
                if (request.Args.Count > 256) throw new ArgumentException("too many arguments (256 max)");
                foreach (var argument in request.Args)
                {
                    if (argument is null) throw new ArgumentException("arguments must not contain null");
                    args.Add(argument);
                }
            }
            commandDisplay = executable + (args.Count > 0 ? " " + string.Join(' ', args) : "");
        }

        var outputEncoding = ResolveEncoding(mode, request.Encoding);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        if (request.Env is not null)
        {
            foreach (var pair in request.Env)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0'))
                    throw new ArgumentException($"invalid environment variable name '{pair.Key}'");
                startInfo.Environment[pair.Key] = pair.Value ?? "";
            }
        }
        return startInfo;
    }

    private Encoding ResolveEncoding(string mode, string? requested)
    {
        var value = (requested ?? "auto").Trim().ToLowerInvariant();
        if (value is "utf8" or "utf-8") return new UTF8Encoding(false);
        if (value is "oem" or "console" or "ansi") return OemEncoding();
        if (value is "utf16" or "unicode") return Encoding.Unicode;
        if (value != "auto") throw new ArgumentException("encoding must be auto, utf8, oem or utf16");
        if (mode == "cmd") return OemEncoding();
        // PowerShell writes UTF-8 to a pipe (file redirection uses UTF-16LE, but we do not use it).
        if (mode == "powershell") return new UTF8Encoding(false);
        return new UTF8Encoding(false);
    }

    private static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch { return Encoding.UTF8; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private string? ValidateWorkingDirectory(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        string full;
        try { full = Path.GetFullPath(cwd); }
        catch (Exception ex) { throw new ArgumentException($"invalid cwd: {ex.Message}"); }
        if (!Directory.Exists(full)) throw new ArgumentException($"cwd does not exist: {full}");
        if (_config.AllowedWorkingRoots.Count > 0)
        {
            var allowed = _config.AllowedWorkingRoots.Any(root =>
                string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (!allowed)
                throw new ExecSecurityException($"cwd is outside exec.allowedWorkingRoots: {full}");
        }
        return full;
    }

    private void ValidateAllowedExecutable(string executable, string mode)
    {
        if (_config.AllowAny) return;
        if (mode != "direct" && !_config.AllowShell)
            throw new ExecSecurityException("shell execution is disabled; set exec.allowShell=true");
        var leaf = Path.GetFileName(executable);
        var full = Path.GetFullPath(executable);
        var allowed = _config.AllowedExecutables.Any(item =>
        {
            if (string.Equals(item, "*", StringComparison.Ordinal)) return true;
            if (string.Equals(item, leaf, StringComparison.OrdinalIgnoreCase)) return true;
            try { return string.Equals(Path.GetFullPath(item), full, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        if (!allowed)
            throw new ExecSecurityException(
                $"executable '{leaf}' is not in exec.allowedExecutables; add it by name or full path, or set exec.allowAny=true");
    }

    private static string ResolveExecutable(string name)
    {
        if (Path.IsPathRooted(name))
        {
            if (File.Exists(name)) return Path.GetFullPath(name);
            throw new ArgumentException($"executable not found: {name}");
        }
        if (File.Exists(name)) return Path.GetFullPath(name);

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var suffix in new[] { "", ".exe", ".cmd", ".bat", ".com" })
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name + suffix);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { /* ignore malformed PATH entries */ }
            }
        }
        throw new ArgumentException($"executable not found on PATH: {name} (configure an absolute path or add it to PATH)");
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                List<ProcessJob> remove;
                lock (_gate)
                {
                    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-_config.JobRetentionSeconds);
                    remove = _jobs.Values
                        .Where(job => job.IsCompleted && job.ExitedAtUtc is { } exited && exited < cutoff)
                        .ToList();
                    foreach (var job in remove) _jobs.Remove(job.Id);

                    if (_jobs.Count > _config.MaxJobs * 4)
                    {
                        foreach (var job in _jobs.Values
                                     .Where(job => job.IsCompleted)
                                     .OrderBy(job => job.ExitedAtUtc ?? job.StartedAtUtc)
                                     .Take(_jobs.Count - _config.MaxJobs * 4))
                        {
                            _jobs.Remove(job.Id);
                            remove.Add(job);
                        }
                    }
                }
                foreach (var job in remove) job.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        ProcessJob[] jobs;
        lock (_gate) jobs = _jobs.Values.ToArray();
        foreach (var job in jobs)
        {
            try { job.Kill(); } catch { /* ignore */ }
        }
        foreach (var job in jobs)
        {
            try { job.Dispose(); } catch { /* ignore */ }
        }
        // Do not dispose _disposeCts immediately: the cleanup loop may still be unwinding
        // from the cancellation and holding its token.
    }
}

public sealed class ProcessJob : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _process = new();
    private readonly TextRingBuffer _stdout;
    private readonly TextRingBuffer _stderr;
    private readonly CancellationTokenSource _timeoutCts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _logger;
    private readonly int _timeoutMs;
    private Task _stdoutPump = Task.CompletedTask;
    private Task _stderrPump = Task.CompletedTask;
    private string _state = "starting";
    private int? _processId;
    private int? _exitCode;
    private DateTimeOffset? _exitedAtUtc;
    private string? _error;
    private bool _killed;
    private bool _timedOut;
    private bool _disposed;

    public string Id { get; }
    public string Mode { get; }
    public string Command { get; }
    public string? Cwd { get; }
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExitedAtUtc { get { lock (_gate) return _exitedAtUtc; } }
    public Task Completion => _completion.Task;
    public bool IsCompleted { get { lock (_gate) return _completion.Task.IsCompleted; } }

    public ProcessJob(string id, string mode, string command, string? cwd, int timeoutMs, int outputCapacity,
        ILogger logger)
    {
        Id = id;
        Mode = mode;
        Command = command;
        Cwd = cwd;
        _timeoutMs = timeoutMs;
        _stdout = new TextRingBuffer(outputCapacity);
        _stderr = new TextRingBuffer(outputCapacity);
        _logger = logger;
        _timeoutCts.CancelAfter(timeoutMs);
    }

    public async Task StartAsync(ProcessStartInfo startInfo, string? stdin, bool closeStdin,
        CancellationToken cancellationToken)
    {
        _process.StartInfo = startInfo;
        try
        {
            if (!_process.Start())
                throw new InvalidOperationException($"failed to start {startInfo.FileName}");
            lock (_gate)
            {
                _state = "running";
                _processId = _process.Id;
            }
            _logger.LogInformation("exec started: {JobId} mode={Mode} pid={Pid} timeout={Timeout}ms",
                Id, Mode, _process.Id, _timeoutMs);

            if (stdin is not null)
            {
                await _process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
            if (closeStdin) _process.StandardInput.Close();

            _stdoutPump = PumpAsync(_process.StandardOutput, _stdout);
            _stderrPump = PumpAsync(_process.StandardError, _stderr);
            _ = WatchAsync();
        }
        catch (Exception ex)
        {
            KillProcess();
            lock (_gate)
            {
                _state = "start_failed";
                _error = ex.Message;
                _exitedAtUtc = DateTimeOffset.UtcNow;
            }
            _completion.TrySetResult();
            throw;
        }
    }

    private async Task PumpAsync(StreamReader reader, TextRingBuffer buffer)
    {
        var chunk = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(chunk, CancellationToken.None);
                if (read <= 0) break;
                buffer.Append(new string(chunk, 0, read));
            }
        }
        catch
        {
            // The process was killed or the pipe closed; the final snapshot still has what arrived.
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_killed)
            {
                lock (_gate) _timedOut = true;
                KillProcess();
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _error = ex.Message;
        }

        try { await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* best effort drain */ }

        int? code = null;
        try { code = _process.ExitCode; } catch { /* ignore */ }
        lock (_gate)
        {
            _exitCode = code;
            _exitedAtUtc = DateTimeOffset.UtcNow;
            _state = _killed ? "killed" : _timedOut ? "timed_out" : code == 0 ? "succeeded" : "failed";
        }
        _logger.LogInformation("exec finished: {JobId} state={State} exit={ExitCode}", Id, _state, code);
        _completion.TrySetResult();
        try { _timeoutCts.Dispose(); } catch { /* ignore */ }
    }

    private void KillProcess()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }

    public void Kill()
    {
        lock (_gate)
        {
            if (_completion.Task.IsCompleted) return;
            _killed = true;
        }
        try { _timeoutCts.Cancel(); } catch { /* ignore */ }
        KillProcess();
    }

    public async Task WriteInputAsync(string text, bool close, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_completion.Task.IsCompleted) throw new InvalidOperationException($"job '{Id}' has already finished");
        }
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
            if (close) _process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            throw new IOException($"cannot write to job stdin: {ex.Message}", ex);
        }
    }

    public JobSnapshot Snapshot(int tailChars)
    {
        tailChars = Math.Clamp(tailChars, 0, 262144);
        var stdout = tailChars == 0 ? ("", false) : _stdout.Tail(tailChars);
        var stderr = tailChars == 0 ? ("", false) : _stderr.Tail(tailChars);
        lock (_gate)
        {
            return new JobSnapshot
            {
                JobId = Id,
                State = _state,
                Mode = Mode,
                Command = Command,
                Cwd = Cwd,
                ProcessId = _processId,
                StartedAtUtc = StartedAtUtc,
                ExitedAtUtc = _exitedAtUtc,
                DurationMs = (int)((_exitedAtUtc ?? DateTimeOffset.UtcNow) - StartedAtUtc).TotalMilliseconds,
                ExitCode = _exitCode,
                TimedOut = _timedOut,
                Killed = _killed,
                Error = _error,
                StdoutTail = stdout.Item1,
                StderrTail = stderr.Item1,
                Truncated = stdout.Item2 || stderr.Item2,
                TotalStdoutChars = _stdout.TotalChars,
                TotalStderrChars = _stderr.TotalChars,
            };
        }
    }

    public ExecResult ToExecResult()
    {
        var snapshot = Snapshot(262144);
        return new ExecResult
        {
            JobId = snapshot.JobId,
            State = snapshot.State,
            ExitCode = snapshot.ExitCode,
            TimedOut = snapshot.TimedOut,
            DurationMs = snapshot.DurationMs,
            Truncated = snapshot.Truncated,
            Stdout = snapshot.StdoutTail,
            Stderr = snapshot.StderrTail,
            Error = snapshot.Error,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.Dispose(); } catch { /* ignore */ }
        try { _timeoutCts.Dispose(); } catch { /* ignore */ }
    }
}

public sealed class ExecRequest
{
    public string Mode { get; set; } = "cmd";
    public string Command { get; set; } = "";
    public List<string>? Args { get; set; }
    public string? Cwd { get; set; }
    public int? TimeoutMs { get; set; }
    public string? Encoding { get; set; }
    public string? Stdin { get; set; }
    public bool? CloseStdin { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

public sealed class ExecResult
{
    public string JobId { get; set; } = "";
    public string State { get; set; } = "";
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public int DurationMs { get; set; }
    public bool Truncated { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public string? Error { get; set; }
    public bool Ok => State == "succeeded" && !TimedOut;
}

public sealed class JobSnapshot
{
    public string JobId { get; set; } = "";
    public string State { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Cwd { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? ExitedAtUtc { get; set; }
    public int DurationMs { get; set; }
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public bool Killed { get; set; }
    public string? Error { get; set; }
    public string StdoutTail { get; set; } = "";
    public string StderrTail { get; set; } = "";
    public bool Truncated { get; set; }
    public long TotalStdoutChars { get; set; }
    public long TotalStderrChars { get; set; }
}

public sealed class ExecSnapshot
{
    public bool Enabled { get; set; }
    public bool AllowAny { get; set; }
    public bool AllowShell { get; set; }
    public List<string> AllowedExecutables { get; set; } = new();
    public List<string> AllowedWorkingRoots { get; set; } = new();
    public int RunningJobs { get; set; }
    public int RetainedJobs { get; set; }
    public int MaxJobs { get; set; }
}

public sealed class WindowsFileWriteResult
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
}

public sealed class WindowsFileReadResult
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public byte[] Data { get; set; } = [];
}

public sealed class WindowsDirEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

public sealed class ExecDisabledException : Exception
{
    public ExecDisabledException(string message) : base(message) { }
}

public sealed class ExecSecurityException : Exception
{
    public ExecSecurityException(string message) : base(message) { }
}
