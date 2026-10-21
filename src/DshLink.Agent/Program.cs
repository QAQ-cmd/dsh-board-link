using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DshLink;

Console.OutputEncoding = Encoding.UTF8;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Double-clicking the exe (no arguments) or launching the desktop shortcut (--gui) starts the
// dashboard without a console window. Any explicit command keeps the normal console CLI.
var guiMode = args.Length == 0 || HasFlag(args, "--gui");
// Bare double-click opens the native dashboard and the tray. `--no-ui` is the
// explicit opt-out for a tray-only background agent.
var noUi = HasFlag(args, "--no-ui");
if (guiMode) Win32Ui.HideConsole();

if (HasFlag(args, "--help") || HasFlag(args, "-h") || args.Contains("help"))
{
    PrintHelp();
    return 0;
}

var configPath = GetOption(args, "--config") ?? AgentConfig.DefaultPath;

if (args.Contains("init"))
{
    var force = HasFlag(args, "--force");
    if (File.Exists(configPath) && !force)
    {
        Console.Error.WriteLine($"config already exists: {configPath}");
        Console.Error.WriteLine("Use `dsh-link init --force` to replace it (a backup is kept).");
        return 2;
    }
    if (File.Exists(configPath) && force)
    {
        var backup = $"{configPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(configPath, backup, overwrite: true);
        Console.WriteLine($"backup: {backup}");
    }
    var created = AgentConfig.CreateDefault();
    created.ConfigPath = configPath;
    created.Save(configPath);
    Console.WriteLine($"created: {configPath}");
    Console.WriteLine($"agent token: {created.Agent.Token}");
    Console.WriteLine("Next: set tunnel.ssh.target, add your SSH public key to the server, then run `dsh-link run`.");
    return 0;
}

var configExisted = File.Exists(Path.GetFullPath(configPath));
AgentConfig config;
try
{
    config = AgentConfig.Load(configPath);
    if (!configExisted)
    {
        Console.WriteLine($"created config: {config.ConfigPath}");
        Console.WriteLine($"agent token:    {config.Agent.Token}");
        Console.WriteLine("Set DSH_LINK_TOKEN to this value on the DSH server (or put it in the plugin config),");
        Console.WriteLine("then edit tunnel.ssh.target/identityFile and run `dsh-link doctor`.");
    }
    var envToken = Environment.GetEnvironmentVariable("DSH_LINK_TOKEN");
    if (!string.IsNullOrWhiteSpace(envToken)) config.Agent.Token = envToken.Trim();
    if (HasFlag(args, "--no-tunnel")) config.Tunnel.Enabled = false;
    var listen = GetOption(args, "--listen");
    if (!string.IsNullOrWhiteSpace(listen)) config.Agent.Listen = listen;
    var port = GetOption(args, "--port");
    if (!string.IsNullOrWhiteSpace(port) && int.TryParse(port, out var parsedPort)) config.Agent.Port = parsedPort;
    config.Normalize();
}
catch (Exception ex)
{
    var message = $"config error: {ex.Message}\nconfig path: {configPath}";
    if (guiMode) Win32Ui.Error(message);
    else Console.Error.WriteLine(message);
    return 1;
}

if (guiMode && await IsAgentRunning(config))
{
    if (noUi) return 0;
    if (await TryRequestUiOpenAsync(config)) return 0;
    var url = DashboardUrl(config);
    if (!Win32Ui.OpenAppWindow(url)) Win32Ui.OpenBrowser(url);
    return 0;
}

if (args.Contains("print-token"))
{
    Console.WriteLine(config.Agent.Token);
    return 0;
}

if (args.Contains("ui"))
{
    return await OpenDashboardAsync(config);
}

if (args.Contains("status"))
{
    return await PrintRemoteStatusAsync(config);
}

if (args.Contains("doctor"))
{
    return RunDoctor(config);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(),
    ApplicationName = typeof(AgentConfig).Assembly.GetName().Name,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
    options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
});
builder.Logging.AddProvider(new FileLoggerProvider(config.Logging));
builder.Logging.SetMinimumLevel(ParseLogLevel(config.Logging.Level));

builder.WebHost.UseUrls($"http://{config.Agent.Listen}:{config.Agent.Port}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = config.Agent.MaxBodyBytes;
    options.AddServerHeader = false;
});
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<SerialManager>();
builder.Services.AddSingleton<AdbService>();
builder.Services.AddSingleton<TunnelManager>();
builder.Services.AddSingleton<ExecService>();
builder.Services.AddSingleton<InteractionLog>();
builder.Services.AddSingleton<DeviceIOLog>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    app.Logger.LogError(feature?.Error, "unhandled request failure");
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new { error = "internal agent error", detail = feature?.Error?.Message });
}));
app.UseMiddleware<AgentAuthMiddleware>();
app.UseMiddleware<InteractionLogMiddleware>();
app.MapGet("/healthz", () => Results.Json(new { ok = true }));
ApiEndpoints.Map(app, config, app.Services.GetRequiredService<SerialManager>(),
    app.Services.GetRequiredService<AdbService>(), app.Services.GetRequiredService<TunnelManager>(),
    app.Services.GetRequiredService<ExecService>(), DateTimeOffset.UtcNow);
DashboardEndpoints.Map(app, config, app.Services.GetRequiredService<TunnelManager>(),
    app.Services.GetRequiredService<ExecService>(), app.Services.GetRequiredService<InteractionLog>(),
    app.Services.GetRequiredService<DeviceIOLog>());

app.Lifetime.ApplicationStarted.Register(() =>
{
    var tunnel = app.Services.GetRequiredService<TunnelManager>();
    tunnel.Start();
    var adb = app.Services.GetRequiredService<AdbService>();
    _ = adb.GetSnapshotAsync(force: true, CancellationToken.None);
    var serial = app.Services.GetRequiredService<SerialManager>();
    _ = serial.CleanupLoopAsync(app.Lifetime.ApplicationStopping);
    if (guiMode)
    {
        TrayHost.Start(DashboardUrl(config), config.Agent.Token,
            Path.Combine(config.Logging.Directory, "agent.log"),
            () => app.Lifetime.StopApplication());
    }
    app.Logger.LogInformation("dashboard open requested: guiMode={GuiMode} ui={Ui} noUi={NoUi}", guiMode, HasFlag(args, "--ui"), noUi);
    if (!noUi && (guiMode || HasFlag(args, "--ui")))
    {
        var url = DashboardUrl(config);
        if (!TrayHost.OpenDashboard(url))
        {
            Win32Ui.CloseAppWindow();
            var opened = Win32Ui.OpenAppWindow(url);
            if (!opened)
            {
                app.Logger.LogWarning("dashboard window failed: {Error}", Win32Ui.LastError);
                var browser = Win32Ui.OpenBrowser(url);
                if (!browser) app.Logger.LogWarning("dashboard browser failed: {Error}", Win32Ui.LastError);
            }
        }
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    TrayHost.Stop();
    Win32Ui.CloseAppWindow();
    try { app.Services.GetRequiredService<TunnelManager>().StopAsync().GetAwaiter().GetResult(); }
    catch { /* ignore */ }
    try { app.Services.GetRequiredService<SerialManager>().Dispose(); }
    catch { /* ignore */ }
    try { app.Services.GetRequiredService<ExecService>().Dispose(); }
    catch { /* ignore */ }
});

app.Logger.LogInformation("dsh-link {Version} listening on http://{Listen}:{Port}", GetVersion(), config.Agent.Listen, config.Agent.Port);
app.Logger.LogInformation("config: {Path}", config.ConfigPath);
app.Logger.LogInformation("tunnel: enabled={Enabled} target={Target}", config.Tunnel.Enabled, config.Tunnel.Ssh.Target);
if (config.Agent.Listen is not ("127.0.0.1" or "localhost" or "::1"))
    app.Logger.LogWarning("agent is bound to {Listen}; the token is the only protection. Prefer 127.0.0.1.", config.Agent.Listen);
if (config.Agent.Token.Length < 32 || config.Agent.Token.Contains("REPLACE", StringComparison.OrdinalIgnoreCase))
    app.Logger.LogWarning("agent.token looks weak or is still the example placeholder; generate a long random token");
if (config.Agent.Listen is "localhost")
    app.Logger.LogWarning("use 127.0.0.1 rather than localhost so the agent binds IPv4 inside the SSH remote forward.");

try
{
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    if (guiMode) Win32Ui.Error($"Agent stopped with an error:\n{ex.Message}");
    Console.Error.WriteLine($"fatal: {ex}");
    return 1;
}

static bool HasFlag(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1];
        if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            return args[i][(name.Length + 1)..];
    }
    return null;
}

static string GetVersion() => typeof(AgentConfig).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

static LogLevel ParseLogLevel(string value) => value switch
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

static async Task<int> PrintRemoteStatusAsync(AgentConfig config)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.Agent.Token);
        var host = config.Agent.Listen is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : config.Agent.Listen;
        var url = $"http://{host}:{config.Agent.Port}/v1/health";
        var json = await client.GetStringAsync(url);
        using var document = JsonDocument.Parse(json);
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"cannot reach agent at http://{config.Agent.Listen}:{config.Agent.Port}: {ex.Message}");
        return 1;
    }
}

static int RunDoctor(AgentConfig config)
{
    var ok = true;
    Console.WriteLine($"config: {config.ConfigPath}");
    Console.WriteLine($"agent:  http://{config.Agent.Listen}:{config.Agent.Port}");
    Console.WriteLine($"token:  {(string.IsNullOrWhiteSpace(config.Agent.Token) ? "MISSING" : "configured")}");
    Console.WriteLine($"tunnel: enabled={config.Tunnel.Enabled} target={config.Tunnel.Ssh.Target}");

    var ssh = config.Tunnel.Ssh.SshPath;
    var sshFound = FindOnPath(ssh);
    Console.WriteLine($"ssh:    {(sshFound is null ? "NOT FOUND" : sshFound)}");
    if (sshFound is null) ok = false;

    var adb = FindOnPath(config.Adb.Path);
    Console.WriteLine($"adb:    {(adb is null ? "NOT FOUND (serial still works)" : adb)}");

    foreach (var forward in config.Tunnel.Forwards)
    {
        var conflict = IsPortListening(forward.ListenHost, forward.ListenPort);
        Console.WriteLine($"forward {forward.Name}: {forward.Direction} {forward.ListenHost}:{forward.ListenPort} -> {forward.TargetHost}:{forward.TargetPort}" +
                          (conflict ? "  [listen port is already in use]" : ""));
        if (forward.Direction == "local" && conflict) ok = false;
    }

    if (config.Tunnel.Enabled && string.IsNullOrWhiteSpace(config.Tunnel.Ssh.Target))
    {
        Console.WriteLine("tunnel.ssh.target is empty");
        ok = false;
    }

    Console.WriteLine(ok ? "doctor: OK" : "doctor: problems found");
    return ok ? 0 : 1;
}

static string? FindOnPath(string name)
{
    if (File.Exists(name)) return Path.GetFullPath(name);
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        foreach (var candidate in new[] { name, name + ".exe" })
        {
            try
            {
                var full = Path.Combine(directory.Trim(), candidate);
                if (File.Exists(full)) return full;
            }
            catch { /* ignore */ }
        }
    }
    return null;
}

static bool IsPortListening(string host, int port)
{
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        var task = client.ConnectAsync(host, port);
        return task.Wait(200) && client.Connected;
    }
    catch { return false; }
}

static string DashboardUrl(AgentConfig config)
{
    var host = config.Agent.Listen is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : config.Agent.Listen;
    return $"http://{host}:{config.Agent.Port}/ui?token={Uri.EscapeDataString(config.Agent.Token)}";
}

async Task<int> OpenDashboardAsync(AgentConfig config)
{
    var url = DashboardUrl(config);
    Console.WriteLine(url);
    if (await IsAgentRunning(config))
    {
        if (await TryRequestUiOpenAsync(config)) return 0;
    }
    else
    {
        Win32Ui.CloseAppWindow();
        StartAgentHidden(config);
        if (!await WaitForAgentAsync(config, TimeSpan.FromSeconds(12)))
        {
            var failed = $"Agent did not start on http://127.0.0.1:{config.Agent.Port}.\n" +
                         $"Check {Path.Combine(config.Logging.Directory, "agent.log")}.";
            if (guiMode) Win32Ui.Error(failed);
            else Console.Error.WriteLine(failed);
            return 1;
        }
    }
    if (!Win32Ui.OpenAppWindow(url) && !Win32Ui.OpenBrowser(url))
    {
        var message = "Could not open the dashboard. Open this URL manually:\n" + url;
        if (guiMode) Win32Ui.Error(message);
        else Console.Error.WriteLine(message);
    }
    return 0;
}

static async Task<bool> IsAgentRunning(AgentConfig config)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var host = config.Agent.Listen is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : config.Agent.Listen;
        using var response = await client.GetAsync($"http://{host}:{config.Agent.Port}/healthz");
        return response.IsSuccessStatusCode;
    }
    catch { return false; }
}

static void StartAgentHidden(AgentConfig config)
{
    var exe = Environment.ProcessPath;
    if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        exe = Path.Combine(AppContext.BaseDirectory, "dsh-link.exe");
    if (!File.Exists(exe)) return;
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(config.ConfigPath);
        Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "dsh-link-launch-error.log"),
            $"{DateTimeOffset.Now:O} {ex}\n");
    }
}

static async Task<bool> TryRequestUiOpenAsync(AgentConfig config)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.Agent.Token);
        using var response = await client.PostAsync($"http://127.0.0.1:{config.Agent.Port}/v1/ui/open", null);
        return response.IsSuccessStatusCode;
    }
    catch { return false; }
}

static async Task<bool> WaitForAgentAsync(AgentConfig config, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (await IsAgentRunning(config)) return true;
        await Task.Delay(300);
    }
    return false;
}

static void PrintHelp()
{
    Console.WriteLine("""
dsh-link - Windows bridge for DeepSeek Harness (SSH tunnels + serial + ADB)

Usage:
  dsh-link [run] [--config <path>] [--no-tunnel] [--listen <ip>] [--port <port>] [--ui]
  dsh-link [--config <path>]        （双击默认：静默启动后台 Agent，不弹浏览器）
  dsh-link --gui [--config <path>]  （无黑框，打开本地 Edge 控制台）
  dsh-link ui [--config <path>]
  dsh-link init [--config <path>] [--force]
  dsh-link status [--config <path>]
  dsh-link doctor [--config <path>]
  dsh-link print-token [--config <path>]

The default config is %LOCALAPPDATA%\DshLink\config.json.
The HTTP API binds to 127.0.0.1 by default and requires:
  Authorization: Bearer <agent.token>

Typical flow:
  1. dsh-link init
  2. edit tunnel.ssh.target, identityFile and forwards
  3. dsh-link doctor
  4. dsh-link run
  5. dsh-link ui    （打开图形化控制台，也可在 run 时加 --ui）
""");
}
