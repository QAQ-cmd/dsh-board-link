using System.Diagnostics;

namespace DshLink;

/// <summary>
/// Owns the outbound OpenSSH process. One SSH connection carries both directions:
/// local forwards expose the server's DSH UI on this PC, remote forwards expose the
/// agent's loopback HTTP endpoint on the server.
/// </summary>
public sealed class TunnelManager : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly ILogger<TunnelManager> _logger;
    private readonly object _gate = new();
    private readonly Queue<string> _recentLog = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _process;
    private string _state = "stopped";
    private string? _lastError;
    private int? _lastExitCode;
    private DateTimeOffset? _startedAtUtc;
    private int _restarts;
    private bool _stopping;

    public TunnelManager(AgentConfig config, ILogger<TunnelManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _stopping = false;
            _cts = new CancellationTokenSource();
            _state = _config.Tunnel.Enabled ? "starting" : "disabled";
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        Process? process;
        lock (_gate)
        {
            _stopping = true;
            cts = _cts;
            loop = _loop;
            process = _process;
            _state = "stopped";
        }

        // Kill the child before cancelling the wait loop: cancelling first lets the loop
        // dispose its Process object while the real ssh.exe is still running.
        if (process is not null)
        {
            try { if (!SafeHasExited(process)) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        try { cts?.Cancel(); } catch { /* ignore */ }
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        }
        cts?.Dispose();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    public TunnelStatus GetStatus()
    {
        lock (_gate)
        {
            return new TunnelStatus
            {
                Enabled = _config.Tunnel.Enabled,
                Provider = _config.Tunnel.Provider,
                State = _state,
                Target = _config.Tunnel.Ssh.Target,
                Pid = TryGetPid(_process),
                StartedAtUtc = _startedAtUtc,
                Restarts = _restarts,
                LastExitCode = _lastExitCode,
                LastError = _lastError,
                Forwards = _config.Tunnel.Forwards.Select(CloneRule).ToList(),
                RecentLog = _recentLog.ToList(),
            };
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_config.Tunnel.Enabled)
        {
            SetState("disabled");
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.Tunnel.Ssh.Target))
        {
            SetState("not-configured", "tunnel.ssh.target is empty; set it to user@server and enable the tunnel");
            _logger.LogWarning("tunnel is enabled but tunnel.ssh.target is empty");
            return;
        }
        if (!_config.Tunnel.Provider.Equals("ssh", StringComparison.OrdinalIgnoreCase))
        {
            SetState("failed", $"unsupported tunnel.provider '{_config.Tunnel.Provider}' (only 'ssh' is implemented)");
            return;
        }

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                SetState(attempt == 0 ? "starting" : "backoff");
                process = StartSshProcess();
                lock (_gate)
                {
                    _process = process;
                    _startedAtUtc = DateTimeOffset.UtcNow;
                    _state = "running";
                    _lastError = null;
                }
                _logger.LogInformation("ssh tunnel started: pid={Pid} target={Target}", process.Id, _config.Tunnel.Ssh.Target);
                AppendLog($"ssh started (pid {process.Id}) -> {_config.Tunnel.Ssh.Target}");

                await process.WaitForExitAsync(cancellationToken);
                var code = SafeExitCode(process);
                var uptime = DateTimeOffset.UtcNow - (_startedAtUtc ?? DateTimeOffset.UtcNow);
                lock (_gate)
                {
                    _lastExitCode = code;
                    _process = null;
                    _startedAtUtc = null;
                    if (!_stopping) _state = "backoff";
                }
                _logger.LogWarning("ssh tunnel exited with code {Code}", code);
                AppendLog($"ssh exited with code {code}");

                if (!_config.Tunnel.Restart.Enabled)
                {
                    SetState("failed", $"ssh exited with code {code}; automatic restart is disabled");
                    return;
                }

                if (uptime > TimeSpan.FromSeconds(60)) attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (process is not null && !SafeHasExited(process)) process.Kill(entireProcessTree: true); }
                catch { /* ignore */ }
                break;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _lastError = ex.Message;
                    _process = null;
                    _startedAtUtc = null;
                    if (!_stopping) _state = "backoff";
                }
                _logger.LogError(ex, "ssh tunnel failed");
                AppendLog($"ssh failed: {ex.Message}");
                if (!_config.Tunnel.Restart.Enabled)
                {
                    SetState("failed", ex.Message);
                    return;
                }
            }
            finally
            {
                process?.Dispose();
            }

            if (cancellationToken.IsCancellationRequested || _stopping) break;
            attempt++;
            _restarts++;
            var delayMs = Math.Min(
                _config.Tunnel.Restart.MaxDelayMs,
                _config.Tunnel.Restart.InitialDelayMs * Math.Pow(2, Math.Min(attempt - 1, 10)));
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!_stopping) SetState("stopped");
    }

    private Process StartSshProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(_config.Tunnel.Ssh.SshPath) ? "ssh" : _config.Tunnel.Ssh.SshPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        var args = startInfo.ArgumentList;
        args.Add("-N");
        args.Add("-T");
        args.Add("-o"); args.Add("BatchMode=yes");
        args.Add("-o"); args.Add("ExitOnForwardFailure=yes");
        args.Add("-o"); args.Add("ServerAliveInterval=" + Math.Max(5, _config.Tunnel.Ssh.ServerAliveIntervalSeconds));
        args.Add("-o"); args.Add("ServerAliveCountMax=" + Math.Max(1, _config.Tunnel.Ssh.ServerAliveCountMax));
        args.Add("-o"); args.Add("ConnectTimeout=" + Math.Max(1, _config.Tunnel.Ssh.ConnectTimeoutSeconds));
        args.Add("-o"); args.Add("StrictHostKeyChecking=" + _config.Tunnel.Ssh.StrictHostKeyChecking);
        if (!string.IsNullOrWhiteSpace(_config.Tunnel.Ssh.KnownHostsFile))
        {
            args.Add("-o");
            args.Add("UserKnownHostsFile=" + _config.Tunnel.Ssh.KnownHostsFile);
        }
        if (_config.Tunnel.Ssh.Port != 22)
        {
            args.Add("-p");
            args.Add(_config.Tunnel.Ssh.Port.ToString());
        }
        if (!string.IsNullOrWhiteSpace(_config.Tunnel.Ssh.IdentityFile))
        {
            args.Add("-i");
            args.Add(_config.Tunnel.Ssh.IdentityFile);
        }
        foreach (var option in _config.Tunnel.Ssh.ExtraOptions)
        {
            if (string.IsNullOrWhiteSpace(option.Key) || option.Key.Contains(' ') || option.Key.Contains('='))
                throw new InvalidOperationException($"invalid ssh extra option name '{option.Key}'");
            args.Add("-o");
            args.Add($"{option.Key}={option.Value}");
        }
        foreach (var forward in _config.Tunnel.Forwards)
        {
            var spec = $"{forward.ListenHost}:{forward.ListenPort}:{forward.TargetHost}:{forward.TargetPort}";
            args.Add(forward.Direction == "remote" ? "-R" : "-L");
            args.Add(spec);
        }
        args.Add(_config.Tunnel.Ssh.Target);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AppendLog(e.Data);
            if (e.Data.Contains("remote port forwarding failed", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate) _lastError = e.Data;
                _logger.LogWarning("ssh: {Line}", e.Data);
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("failed to start ssh");
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    private void SetState(string state, string? error = null)
    {
        lock (_gate)
        {
            _state = state;
            if (error is not null) _lastError = error;
        }
    }

    private void AppendLog(string line)
    {
        lock (_gate)
        {
            _recentLog.Enqueue($"{DateTimeOffset.Now:HH:mm:ss} {line}");
            while (_recentLog.Count > 100) _recentLog.Dequeue();
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static int? TryGetPid(Process? process)
    {
        if (process is null) return null;
        try { return process.HasExited ? null : process.Id; } catch { return null; }
    }

    private static ForwardRule CloneRule(ForwardRule rule) => new()
    {
        Name = rule.Name,
        Direction = rule.Direction,
        ListenHost = rule.ListenHost,
        ListenPort = rule.ListenPort,
        TargetHost = rule.TargetHost,
        TargetPort = rule.TargetPort,
    };

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class TunnelStatus
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "ssh";
    public string State { get; set; } = "stopped";
    public string? Target { get; set; }
    public int? Pid { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public int Restarts { get; set; }
    public int? LastExitCode { get; set; }
    public string? LastError { get; set; }
    public List<ForwardRule> Forwards { get; set; } = new();
    public List<string> RecentLog { get; set; } = new();
}
