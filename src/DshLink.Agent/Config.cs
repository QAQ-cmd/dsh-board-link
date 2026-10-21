using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLink;

public sealed class AgentConfig
{
    public AgentSection Agent { get; set; } = new();
    public TunnelSection Tunnel { get; set; } = new();
    public SerialSection Serial { get; set; } = new();
    public AdbSection Adb { get; set; } = new();
    public ExecSection Exec { get; set; } = new();
    public LoggingSection Logging { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("DSH_LINK_CONFIG");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshLink",
                "config.json");
        }
    }

    public static AgentConfig Load(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            var created = CreateDefault();
            created.ConfigPath = path;
            created.Save(path);
            return created;
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException($"config file is empty or not a JSON object: {path}");
        config.ConfigPath = path;
        config.Normalize();
        return config;
    }

    public static AgentConfig CreateDefault()
    {
        var config = new AgentConfig();
        config.Agent.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        config.Normalize();
        return config;
    }

    public void Save(string path)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        lock (this)
        {
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, path, overwrite: true);
                ConfigPath = path;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }

    public void Normalize()
    {
        Agent ??= new AgentSection();
        Tunnel ??= new TunnelSection();
        Serial ??= new SerialSection();
        Adb ??= new AdbSection();
        Exec ??= new ExecSection();
        Logging ??= new LoggingSection();

        Agent.Listen = string.IsNullOrWhiteSpace(Agent.Listen) ? "127.0.0.1" : Agent.Listen.Trim();
        if (Agent.Port is < 1 or > 65535) throw new InvalidOperationException("agent.port must be between 1 and 65535");
        if (Agent.MaxBodyBytes < 1024) Agent.MaxBodyBytes = 128 * 1024 * 1024;
        if (string.IsNullOrWhiteSpace(Agent.Token))
            throw new InvalidOperationException(
                "agent.token is empty. Generate one with `dsh-link init --force` or set a long random token in the config file.");

        Tunnel.Ssh ??= new SshSection();
        Tunnel.Restart ??= new RestartSection();
        Tunnel.Forwards ??= new List<ForwardRule>();
        Tunnel.Ssh.Port = Tunnel.Ssh.Port is < 1 or > 65535 ? 22 : Tunnel.Ssh.Port;
        Tunnel.Ssh.StrictHostKeyChecking = string.IsNullOrWhiteSpace(Tunnel.Ssh.StrictHostKeyChecking)
            ? "accept-new"
            : Tunnel.Ssh.StrictHostKeyChecking.Trim();
        Tunnel.Ssh.ExtraOptions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Tunnel.Restart.InitialDelayMs < 100) Tunnel.Restart.InitialDelayMs = 100;
        if (Tunnel.Restart.MaxDelayMs < Tunnel.Restart.InitialDelayMs)
            Tunnel.Restart.MaxDelayMs = Tunnel.Restart.InitialDelayMs;

        foreach (var rule in Tunnel.Forwards)
        {
            rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "forward" : rule.Name.Trim();
            rule.Direction = (rule.Direction ?? "").Trim().ToLowerInvariant();
            if (rule.Direction is not ("local" or "remote"))
                throw new InvalidOperationException($"tunnel forward '{rule.Name}': direction must be 'local' or 'remote'");
            if (rule.ListenPort is < 1 or > 65535)
                throw new InvalidOperationException($"tunnel forward '{rule.Name}': listenPort must be between 1 and 65535");
            if (rule.TargetPort is < 1 or > 65535)
                throw new InvalidOperationException($"tunnel forward '{rule.Name}': targetPort must be between 1 and 65535");
            rule.ListenHost = string.IsNullOrWhiteSpace(rule.ListenHost) ? "127.0.0.1" : rule.ListenHost.Trim();
            rule.TargetHost = string.IsNullOrWhiteSpace(rule.TargetHost) ? "127.0.0.1" : rule.TargetHost.Trim();
        }

        Serial.MaxSessions = Math.Clamp(Serial.MaxSessions, 1, 64);
        Serial.BufferBytes = Math.Clamp(Serial.BufferBytes, 4096, 64 * 1024 * 1024);
        Serial.ReadChunkBytes = Math.Clamp(Serial.ReadChunkBytes, 256, 65536);
        Serial.IdleTimeoutSeconds = Math.Max(0, Serial.IdleTimeoutSeconds);

        Adb.Path = string.IsNullOrWhiteSpace(Adb.Path) ? "adb" : Adb.Path.Trim();
        Adb.DefaultTimeoutMs = Math.Clamp(Adb.DefaultTimeoutMs, 1000, 600000);
        Adb.MaxOutputBytes = Math.Clamp(Adb.MaxOutputBytes, 4096, 64 * 1024 * 1024);
        Adb.MaxTransferBytes = Math.Clamp(Adb.MaxTransferBytes, 1024, 512 * 1024 * 1024);
        var uploadBodyBytes = Adb.MaxTransferBytes * 4 / 3 + 2 * 1024 * 1024;
        if (Agent.MaxBodyBytes < uploadBodyBytes) Agent.MaxBodyBytes = uploadBodyBytes;

        Exec.CmdPath = string.IsNullOrWhiteSpace(Exec.CmdPath) ? "cmd.exe" : Exec.CmdPath.Trim();
        Exec.PowerShellPath = string.IsNullOrWhiteSpace(Exec.PowerShellPath) ? "powershell.exe" : Exec.PowerShellPath.Trim();
        Exec.MaxTimeoutMs = Math.Clamp(Exec.MaxTimeoutMs, 1000, 3_600_000);
        Exec.DefaultTimeoutMs = Math.Clamp(Exec.DefaultTimeoutMs, 1000, Exec.MaxTimeoutMs);
        Exec.MaxOutputBytes = Math.Clamp(Exec.MaxOutputBytes, 4096, 64 * 1024 * 1024);
        Exec.MaxFileBytes = Math.Clamp(Exec.MaxFileBytes, 1024, 512 * 1024 * 1024);
        Exec.MaxJobs = Math.Clamp(Exec.MaxJobs, 1, 32);
        Exec.JobRetentionSeconds = Math.Clamp(Exec.JobRetentionSeconds, 10, 86_400);
        Exec.AllowedExecutables = (Exec.AllowedExecutables ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Exec.AllowedWorkingRoots = (Exec.AllowedWorkingRoots ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(ExpandPath)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Logging.Directory = string.IsNullOrWhiteSpace(Logging.Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLink", "logs")
            : ExpandPath(Logging.Directory);
        Logging.Level = string.IsNullOrWhiteSpace(Logging.Level) ? "information" : Logging.Level.Trim().ToLowerInvariant();
        Logging.MaxFileBytes = Math.Clamp(Logging.MaxFileBytes, 64 * 1024, 256 * 1024 * 1024);
        Logging.RetainedFiles = Math.Clamp(Logging.RetainedFiles, 1, 30);

        Tunnel.Ssh.IdentityFile = ExpandPath(Tunnel.Ssh.IdentityFile);
        Tunnel.Ssh.KnownHostsFile = ExpandPath(Tunnel.Ssh.KnownHostsFile);
    }

    public static string ExpandPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded == "~") expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        return Path.GetFullPath(expanded);
    }
}

public sealed class AgentSection
{
    public string Listen { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8765;
    public string Token { get; set; } = "";
    public long MaxBodyBytes { get; set; } = 128 * 1024 * 1024;
}

public sealed class TunnelSection
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "ssh";
    public SshSection Ssh { get; set; } = new();
    public List<ForwardRule> Forwards { get; set; } = new()
    {
        new ForwardRule
        {
            Name = "dsh-web",
            Direction = "local",
            ListenHost = "127.0.0.1",
            ListenPort = 60573,
            TargetHost = "127.0.0.1",
            TargetPort = 60573,
        },
        new ForwardRule
        {
            Name = "board-link",
            Direction = "remote",
            ListenHost = "127.0.0.1",
            ListenPort = 18765,
            TargetHost = "127.0.0.1",
            TargetPort = 8765,
        },
    };
    public RestartSection Restart { get; set; } = new();
}

public sealed class SshSection
{
    public string Target { get; set; } = "";
    public int Port { get; set; } = 22;
    public string IdentityFile { get; set; } = "";
    public string KnownHostsFile { get; set; } = "";
    public string StrictHostKeyChecking { get; set; } = "accept-new";
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int ServerAliveIntervalSeconds { get; set; } = 15;
    public int ServerAliveCountMax { get; set; } = 3;
    public string SshPath { get; set; } = "ssh";
    public Dictionary<string, string> ExtraOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ForwardRule
{
    public string Name { get; set; } = "forward";
    public string Direction { get; set; } = "local";
    public string ListenHost { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; }
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; }
}

public sealed class RestartSection
{
    public bool Enabled { get; set; } = true;
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;
}

public sealed class SerialSection
{
    public int MaxSessions { get; set; } = 8;
    public int BufferBytes { get; set; } = 1024 * 1024;
    public int ReadChunkBytes { get; set; } = 4096;
    public int IdleTimeoutSeconds { get; set; }
}

public sealed class AdbSection
{
    public string Path { get; set; } = "adb";
    public int DefaultTimeoutMs { get; set; } = 30000;
    public int MaxOutputBytes { get; set; } = 1024 * 1024;
    public long MaxTransferBytes { get; set; } = 64L * 1024 * 1024;
}

public sealed class ExecSection
{
    public bool Enabled { get; set; }
    public bool AllowAny { get; set; }
    public bool AllowShell { get; set; }
    public string CmdPath { get; set; } = "cmd.exe";
    public string PowerShellPath { get; set; } = "powershell.exe";
    public List<string> AllowedExecutables { get; set; } = new()
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "adb.exe",
        "fastboot.exe",
        "python.exe",
        "py.exe",
        "esptool.py",
        "rkdeveloptool.exe",
        "upgrade_tool.exe",
    };
    public List<string> AllowedWorkingRoots { get; set; } = new();
    public int DefaultTimeoutMs { get; set; } = 120000;
    public int MaxTimeoutMs { get; set; } = 600000;
    public int MaxOutputBytes { get; set; } = 4 * 1024 * 1024;
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
    public int MaxJobs { get; set; } = 8;
    public int JobRetentionSeconds { get; set; } = 600;
}

public sealed class LoggingSection
{
    public string Directory { get; set; } = "";
    public string Level { get; set; } = "information";
    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public int RetainedFiles { get; set; } = 3;
}
