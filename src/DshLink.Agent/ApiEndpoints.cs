using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLink;

public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Map(WebApplication app, AgentConfig config, SerialManager serial, AdbService adb,
        TunnelManager tunnel, ExecService exec, DateTimeOffset startedAt)
    {
        app.MapPost("/v1/ui/open", () =>
        {
            var host = config.Agent.Listen is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : config.Agent.Listen;
            var url = $"http://{host}:{config.Agent.Port}/ui?token={Uri.EscapeDataString(config.Agent.Token)}";
            var opened = TrayHost.OpenDashboard(url);
            if (!opened) opened = Win32Ui.OpenAppWindow(url);
            return Ok(new { opened, error = Win32Ui.LastError });
        });


        app.MapPost("/v1/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                lifetime.StopApplication();
            });
            return Ok(new { stopping = true });
        });

        app.MapGet("/v1/health", async (CancellationToken cancellationToken) =>
        {
            var adbSnapshot = await adb.GetSnapshotAsync(force: false, cancellationToken);
            var sessions = serial.ListSessions();
            IReadOnlyList<SerialPortInfo> ports;
            try { ports = serial.GetPorts(); }
            catch { ports = Array.Empty<SerialPortInfo>(); }

            return Ok(new
            {
                ok = true,
                name = "dsh-link",
                version = AgentVersion,
                hostname = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                processId = Environment.ProcessId,
                startedAtUtc = startedAt,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                agent = new
                {
                    listen = $"{config.Agent.Listen}:{config.Agent.Port}",
                },
                tunnel = tunnel.GetStatus(),
                serial = new
                {
                    ports,
                    sessions,
                    maxSessions = config.Serial.MaxSessions,
                },
                adb = new
                {
                    available = adbSnapshot.Available,
                    version = adbSnapshot.Version,
                    path = adbSnapshot.Path,
                    error = adbSnapshot.Error,
                    devices = adbSnapshot.Devices,
                    checkedAtUtc = adbSnapshot.CheckedAtUtc,
                },
                exec = exec.Snapshot(),
            });
        });

        app.MapGet("/v1/config", () => Ok(new
        {
            agent = new
            {
                listen = config.Agent.Listen,
                port = config.Agent.Port,
                tokenConfigured = !string.IsNullOrWhiteSpace(config.Agent.Token),
            },
            tunnel = new
            {
                enabled = config.Tunnel.Enabled,
                provider = config.Tunnel.Provider,
                ssh = new
                {
                    target = config.Tunnel.Ssh.Target,
                    port = config.Tunnel.Ssh.Port,
                    identityFile = config.Tunnel.Ssh.IdentityFile,
                    strictHostKeyChecking = config.Tunnel.Ssh.StrictHostKeyChecking,
                },
                forwards = config.Tunnel.Forwards,
            },
            serial = config.Serial,
            adb = new
            {
                path = config.Adb.Path,
                defaultTimeoutMs = config.Adb.DefaultTimeoutMs,
                maxOutputBytes = config.Adb.MaxOutputBytes,
                maxTransferBytes = config.Adb.MaxTransferBytes,
            },
            exec = exec.Snapshot(),
        }));

        app.MapPost("/v1/tunnel/start", () =>
        {
            config.Tunnel.Enabled = true;
            tunnel.Start();
            return Ok(new { tunnel = tunnel.GetStatus() });
        });

        app.MapPost("/v1/tunnel/stop", async () =>
        {
            config.Tunnel.Enabled = false;
            await tunnel.StopAsync();
            return Ok(new { tunnel = tunnel.GetStatus() });
        });

        app.MapPost("/v1/tunnel/restart", async () =>
        {
            config.Tunnel.Enabled = true;
            await tunnel.RestartAsync();
            return Ok(new { tunnel = tunnel.GetStatus() });
        });

        app.MapGet("/v1/serial/ports", () =>
        {
            try { return Ok(new { ports = serial.GetPorts() }); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapGet("/v1/serial/sessions", () => Ok(new { sessions = serial.ListSessions() }));

        app.MapPost("/v1/serial/open", (SerialOpenRequest request) =>
        {
            try
            {
                var options = new SerialOpenOptions
                {
                    Port = request.Port ?? "",
                    BaudRate = request.BaudRate ?? 115200,
                    DataBits = request.DataBits ?? 8,
                    Parity = request.Parity ?? "none",
                    StopBits = request.StopBits ?? "one",
                    Handshake = request.Handshake ?? "none",
                    Dtr = request.Dtr ?? true,
                    Rts = request.Rts ?? true,
                };
                if (options.BaudRate is < 50 or > 4_000_000)
                    throw new ArgumentException("baudRate must be between 50 and 4000000");
                if (options.DataBits is < 5 or > 8)
                    throw new ArgumentException("dataBits must be between 5 and 8");
                var session = serial.Open(options);
                return Ok(session.Snapshot());
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/serial/read", async (SerialReadRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var session = serial.Get(request.SessionId ?? "");
                var result = await session.ReadAsync(request.TimeoutMs ?? 1000, request.MaxBytes ?? 65536,
                    request.Peek ?? false, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/serial/write", (SerialWriteRequest request) =>
        {
            try
            {
                var session = serial.Get(request.SessionId ?? "");
                var hasText = request.Text is not null;
                var hasBase64 = request.Base64 is not null;
                if (hasText == hasBase64)
                    throw new ArgumentException("provide exactly one of text or base64");

                byte[] data;
                if (hasText)
                {
                    var text = request.Text!;
                    var newline = (request.Newline ?? "none").Trim().ToLowerInvariant();
                    text = newline switch
                    {
                        "none" or "" => text,
                        "lf" => text + "\n",
                        "crlf" => text + "\r\n",
                        _ => throw new ArgumentException("newline must be none, lf or crlf"),
                    };
                    data = Encoding.UTF8.GetBytes(text);
                }
                else
                {
                    try { data = Convert.FromBase64String(request.Base64!); }
                    catch (FormatException) { throw new ArgumentException("base64 is not valid base64"); }
                }

                if (data.Length > 4 * 1024 * 1024) throw new ArgumentException("serial write is limited to 4 MiB");
                var written = session.Write(data);
                return Ok(new
                {
                    sessionId = session.Id,
                    bytesWritten = written,
                    totalBytesWritten = session.TotalBytesWritten,
                });
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/serial/close", (SerialCloseRequest request) =>
        {
            try
            {
                if (!serial.Close(request.SessionId ?? ""))
                    throw new KeyNotFoundException($"serial session '{request.SessionId}' does not exist");
                return Ok(new { closed = true });
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/serial/pulse", async (SerialPulseRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var session = serial.Get(request.SessionId ?? "");
                await session.PulseAsync(request.Line ?? "dtr", request.DurationMs ?? 100, cancellationToken);
                return Ok(new { sessionId = session.Id, line = request.Line, durationMs = request.DurationMs ?? 100 });
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapGet("/v1/adb/devices", async (bool? refresh, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await adb.GetSnapshotAsync(refresh ?? false, cancellationToken);
                return Ok(new { available = snapshot.Available, version = snapshot.Version, error = snapshot.Error, devices = snapshot.Devices });
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapGet("/v1/adb/status", async (bool? refresh, CancellationToken cancellationToken) =>
        {
            try { return Ok(await adb.GetSnapshotAsync(refresh ?? false, cancellationToken)); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/shell", async (AdbShellRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Command)) throw new ArgumentException("command is required");
                var result = await adb.ShellAsync(request.Serial, request.Command,
                    request.TimeoutMs ?? config.Adb.DefaultTimeoutMs, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/logcat", async (AdbLogcatRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await adb.LogcatAsync(request.Serial, request.Lines ?? 200, request.Filter,
                    request.Platform, request.TimeoutMs ?? config.Adb.DefaultTimeoutMs, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/screenshot", async (AdbScreenshotRequest request, CancellationToken cancellationToken) =>
        {
            try { return Ok(await adb.ScreenshotAsync(request.Serial, cancellationToken)); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/push", async (AdbPushRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.RemotePath)) throw new ArgumentException("remotePath is required");
                if (string.IsNullOrWhiteSpace(request.DataBase64)) throw new ArgumentException("dataBase64 is required");
                if (request.DataBase64.Length > config.Adb.MaxTransferBytes * 4L / 3L + 4)
                    throw new ArgumentException($"push exceeds the configured transfer limit of {config.Adb.MaxTransferBytes} bytes");
                byte[] data;
                try { data = Convert.FromBase64String(request.DataBase64); }
                catch (FormatException) { throw new ArgumentException("dataBase64 is not valid base64"); }
                if (data.LongLength > config.Adb.MaxTransferBytes)
                    throw new ArgumentException($"push exceeds the configured transfer limit of {config.Adb.MaxTransferBytes} bytes");
                var result = await adb.PushAsync(request.Serial, request.RemotePath, data, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/pull", async (AdbPullRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.RemotePath)) throw new ArgumentException("remotePath is required");
                var result = await adb.PullAsync(request.Serial, request.RemotePath,
                    request.MaxBytes ?? config.Adb.MaxTransferBytes, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/install", async (AdbInstallRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ApkBase64)) throw new ArgumentException("apkBase64 is required");
                if (request.ApkBase64.Length > config.Adb.MaxTransferBytes * 4L / 3L + 4)
                    throw new ArgumentException($"apk exceeds the configured transfer limit of {config.Adb.MaxTransferBytes} bytes");
                byte[] data;
                try { data = Convert.FromBase64String(request.ApkBase64); }
                catch (FormatException) { throw new ArgumentException("apkBase64 is not valid base64"); }
                if (data.LongLength > config.Adb.MaxTransferBytes)
                    throw new ArgumentException($"apk exceeds the configured transfer limit of {config.Adb.MaxTransferBytes} bytes");
                return Ok(await adb.InstallAsync(request.Serial, data, cancellationToken));
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/adb/reboot", async (AdbRebootRequest request, CancellationToken cancellationToken) =>
        {
            try { return Ok(await adb.RebootAsync(request.Serial, request.Mode, cancellationToken)); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapGet("/v1/exec/status", () => Ok(exec.Snapshot()));

        app.MapPost("/v1/exec", async (ExecRequest request, CancellationToken cancellationToken) =>
        {
            try { return Ok(await exec.RunAsync(request, cancellationToken)); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/jobs", async (ExecRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var job = await exec.StartJobAsync(request, cancellationToken);
                return Ok(job.Snapshot(16384));
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapGet("/v1/jobs", () => Ok(new { jobs = exec.ListJobs() }));

        app.MapGet("/v1/jobs/{jobId}", (string jobId, int? tailChars) =>
        {
            try { return Ok(exec.GetJob(jobId).Snapshot(tailChars ?? 16384)); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/jobs/{jobId}/kill", (string jobId) =>
        {
            try { return Ok(exec.KillJob(jobId)); }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/jobs/{jobId}/stdin", async (string jobId, JobInputRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Ok(await exec.WriteJobInputAsync(jobId, request.Text ?? "", request.Close ?? false, cancellationToken));
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/files/write", async (FileWriteRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.DataBase64)) throw new ArgumentException("dataBase64 is required");
                if (request.DataBase64.Length > config.Exec.MaxFileBytes * 4L / 3L + 4)
                    throw new ArgumentException($"file exceeds exec.maxFileBytes={config.Exec.MaxFileBytes}");
                byte[] data;
                try { data = Convert.FromBase64String(request.DataBase64); }
                catch (FormatException) { throw new ArgumentException("dataBase64 is not valid base64"); }
                return Ok(await exec.WriteFileAsync(request.Path, data, request.Overwrite ?? true,
                    request.CreateDirectories ?? true, cancellationToken));
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapPost("/v1/files/read", async (FileReadRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await exec.ReadFileAsync(request.Path, request.MaxBytes ?? config.Exec.MaxFileBytes,
                    cancellationToken);
                return Ok(new
                {
                    path = result.Path,
                    bytes = result.Bytes,
                    dataBase64 = Convert.ToBase64String(result.Data),
                });
            }
            catch (Exception ex) { return Fail(ex); }
        });

        app.MapGet("/v1/files/list", (string path) =>
        {
            try { return Ok(new { path, entries = exec.ListDirectory(path) }); }
            catch (Exception ex) { return Fail(ex); }
        });
    }

    private static string AgentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private static IResult Ok(object value) => Results.Json(value, JsonOptions);

    private static IResult Fail(Exception exception)
    {
        var status = exception switch
        {
            KeyNotFoundException => StatusCodes.Status404NotFound,
            ExecDisabledException or ExecSecurityException => StatusCodes.Status403Forbidden,
            ArgumentException => StatusCodes.Status400BadRequest,
            InvalidOperationException => StatusCodes.Status409Conflict,
            IOException => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Json(new { error = exception.Message, type = exception.GetType().Name }, JsonOptions,
            statusCode: status);
    }
}
