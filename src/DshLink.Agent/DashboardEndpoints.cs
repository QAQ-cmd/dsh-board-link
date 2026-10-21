using System.Reflection;
using System.Text.Json;

namespace DshLink;

public static class DashboardEndpoints
{
    private static readonly Lazy<byte[]> Page = new(LoadPage);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Map(WebApplication app, AgentConfig config, TunnelManager tunnel, ExecService exec,
        InteractionLog interactions, DeviceIOLog deviceIo)
    {
        app.MapGet("/", () => Results.Redirect("/ui"));
        app.MapGet("/ui", ServePage);

        app.MapGet("/ui/api/session", () => Ok(new
        {
            ok = true,
            name = "dsh-link",
            version = Version(),
            hostname = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            configPath = config.ConfigPath,
            token = config.Agent.Token,
            agent = new { listen = config.Agent.Listen, port = config.Agent.Port },
        }));

        app.MapGet("/ui/api/config", () =>
        {
            var dsh = Forward(config, "dsh-web");
            var board = Forward(config, "board-link");
            return Ok(new
            {
                agent = new
                {
                    listen = config.Agent.Listen,
                    port = config.Agent.Port,
                    hostname = Environment.MachineName,
                    version = Version(),
                    configPath = config.ConfigPath,
                    logDirectory = config.Logging.Directory,
                },
                token = config.Agent.Token,
                tunnel = new
                {
                    enabled = config.Tunnel.Enabled,
                    target = config.Tunnel.Ssh.Target,
                    port = config.Tunnel.Ssh.Port,
                    identityFile = config.Tunnel.Ssh.IdentityFile,
                    knownHostsFile = config.Tunnel.Ssh.KnownHostsFile,
                    strictHostKeyChecking = config.Tunnel.Ssh.StrictHostKeyChecking,
                    localDshPort = dsh?.ListenPort ?? 60573,
                    remoteDshPort = dsh?.TargetPort ?? 60573,
                    remoteAgentPort = board?.ListenPort ?? 18765,
                    restartEnabled = config.Tunnel.Restart.Enabled,
                    status = tunnel.GetStatus(),
                },
                exec = new
                {
                    enabled = config.Exec.Enabled,
                    allowAny = config.Exec.AllowAny,
                    allowShell = config.Exec.AllowShell,
                    allowedExecutables = string.Join('\n', config.Exec.AllowedExecutables),
                    allowedWorkingRoots = string.Join('\n', config.Exec.AllowedWorkingRoots),
                    defaultTimeoutMs = config.Exec.DefaultTimeoutMs,
                    maxTimeoutMs = config.Exec.MaxTimeoutMs,
                    maxOutputBytes = config.Exec.MaxOutputBytes,
                    maxFileBytes = config.Exec.MaxFileBytes,
                    maxJobs = config.Exec.MaxJobs,
                    jobRetentionSeconds = config.Exec.JobRetentionSeconds,
                    snapshot = exec.Snapshot(),
                },
            });
        });

        app.MapPost("/ui/api/config", async (UiConfigUpdate update, CancellationToken cancellationToken) =>
        {
            try
            {
                var tunnelBefore = JsonSerializer.Serialize(config.Tunnel, JsonOptions);
                Apply(config, update);
                config.Save(config.ConfigPath);
                var tunnelChanged = tunnelBefore != JsonSerializer.Serialize(config.Tunnel, JsonOptions);
                // Saving the Windows-control page alone must not bounce the SSH tunnel:
                // that would tear down the browser's forwarded DSH port.
                if (tunnelChanged)
                {
                    if (config.Tunnel.Enabled) await tunnel.RestartAsync();
                    else await tunnel.StopAsync();
                }
                return Ok(new { saved = true, tunnelChanged, tunnel = tunnel.GetStatus() });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, JsonOptions, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/ui/api/logs", (int? lines) =>
        {
            try { return Ok(new { text = ReadLogTail(config, lines ?? 200) }); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
        });

        app.MapGet("/ui/api/interactions", (int? limit) =>
            Ok(new { entries = interactions.Snapshot(limit ?? 300) }));

        app.MapGet("/ui/api/device-io", (int? limit) =>
            Ok(new { entries = deviceIo.Snapshot(limit ?? 400) }));

        app.MapPost("/ui/api/device-io/clear", () =>
        {
            deviceIo.Clear();
            return Ok(new { cleared = true });
        });

        app.MapGet("/ui/api/plugin.zip", () =>
        {
            var bytes = PluginPackage.CreateZip();
            return Results.File(bytes, "application/zip", "dsh-board-link.zip");
        });
    }

    private static void Apply(AgentConfig config, UiConfigUpdate update)
    {
        if (update?.Tunnel is { } t)
        {
            if (t.Port is < 1 or > 65535) throw new ArgumentException("SSH port must be between 1 and 65535");
            if (t.LocalDshPort is < 1 or > 65535) throw new ArgumentException("local DSH port must be between 1 and 65535");
            if (t.RemoteDshPort is < 1 or > 65535) throw new ArgumentException("server DSH port must be between 1 and 65535");
            if (t.RemoteAgentPort is < 1 or > 65535) throw new ArgumentException("remote agent port must be between 1 and 65535");

            config.Tunnel.Enabled = t.Enabled ?? config.Tunnel.Enabled;
            if (t.Target is not null) config.Tunnel.Ssh.Target = t.Target.Trim();
            if (t.Port is not null) config.Tunnel.Ssh.Port = t.Port.Value;
            if (t.IdentityFile is not null) config.Tunnel.Ssh.IdentityFile = t.IdentityFile.Trim();
            if (t.KnownHostsFile is not null) config.Tunnel.Ssh.KnownHostsFile = t.KnownHostsFile.Trim();
            if (t.StrictHostKeyChecking is not null)
                config.Tunnel.Ssh.StrictHostKeyChecking = t.StrictHostKeyChecking.Trim();
            if (t.RestartEnabled is not null) config.Tunnel.Restart.Enabled = t.RestartEnabled.Value;

            var dsh = Forward(config, "dsh-web");
            if (dsh is null)
            {
                dsh = new ForwardRule { Name = "dsh-web" };
                config.Tunnel.Forwards.Add(dsh);
            }
            dsh.Direction = "local";
            dsh.ListenHost = "127.0.0.1";
            if (t.LocalDshPort is not null) dsh.ListenPort = t.LocalDshPort.Value;
            dsh.TargetHost = "127.0.0.1";
            if (t.RemoteDshPort is not null) dsh.TargetPort = t.RemoteDshPort.Value;

            var board = Forward(config, "board-link");
            if (board is null)
            {
                board = new ForwardRule { Name = "board-link" };
                config.Tunnel.Forwards.Add(board);
            }
            board.Direction = "remote";
            board.ListenHost = "127.0.0.1";
            if (t.RemoteAgentPort is not null) board.ListenPort = t.RemoteAgentPort.Value;
            board.TargetHost = "127.0.0.1";
            board.TargetPort = config.Agent.Port;
        }

        if (update?.Exec is { } e)
        {
            config.Exec.Enabled = e.Enabled ?? config.Exec.Enabled;
            config.Exec.AllowAny = e.AllowAny ?? config.Exec.AllowAny;
            config.Exec.AllowShell = e.AllowShell ?? config.Exec.AllowShell;
            if (e.AllowedExecutables is not null) config.Exec.AllowedExecutables = ParseLines(e.AllowedExecutables);
            if (e.AllowedWorkingRoots is not null) config.Exec.AllowedWorkingRoots = ParseLines(e.AllowedWorkingRoots);
            if (e.DefaultTimeoutMs is not null) config.Exec.DefaultTimeoutMs = e.DefaultTimeoutMs.Value;
            if (e.MaxTimeoutMs is not null) config.Exec.MaxTimeoutMs = e.MaxTimeoutMs.Value;
            if (e.MaxOutputBytes is not null) config.Exec.MaxOutputBytes = e.MaxOutputBytes.Value;
            if (e.MaxFileBytes is not null) config.Exec.MaxFileBytes = e.MaxFileBytes.Value;
            if (e.MaxJobs is not null) config.Exec.MaxJobs = e.MaxJobs.Value;
            if (e.JobRetentionSeconds is not null) config.Exec.JobRetentionSeconds = e.JobRetentionSeconds.Value;
        }

        config.Normalize();
    }

    private static List<string> ParseLines(string text) => text
        .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !line.StartsWith('#'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static ForwardRule? Forward(AgentConfig config, string name) =>
        config.Tunnel.Forwards.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IResult ServePage(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Results.Bytes(Page.Value, "text/html; charset=utf-8", enableRangeProcessing: false);
    }

    private static byte[] LoadPage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("DshLink.Ui.index.html")
                           ?? throw new InvalidOperationException("embedded dashboard page is missing");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ReadLogTail(AgentConfig config, int lines)
    {
        lines = Math.Clamp(lines, 1, 2000);
        var path = Path.Combine(config.Logging.Directory, "agent.log");
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            queue.Enqueue(line);
            while (queue.Count > lines) queue.Dequeue();
        }
        return string.Join('\n', queue);
    }

    private static IResult Ok(object value) => Results.Json(value, JsonOptions);
    private static string Version() => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
