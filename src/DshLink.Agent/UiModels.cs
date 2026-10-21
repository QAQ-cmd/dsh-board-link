namespace DshLink;

public sealed class UiTunnelSettings
{
    public bool? Enabled { get; set; }
    public string? Target { get; set; }
    public int? Port { get; set; }
    public string? IdentityFile { get; set; }
    public string? KnownHostsFile { get; set; }
    public string? StrictHostKeyChecking { get; set; }
    public int? LocalDshPort { get; set; }
    public int? RemoteDshPort { get; set; }
    public int? RemoteAgentPort { get; set; }
    public bool? RestartEnabled { get; set; }
}

public sealed class UiExecSettings
{
    public bool? Enabled { get; set; }
    public bool? AllowAny { get; set; }
    public bool? AllowShell { get; set; }
    public string? AllowedExecutables { get; set; }
    public string? AllowedWorkingRoots { get; set; }
    public int? DefaultTimeoutMs { get; set; }
    public int? MaxTimeoutMs { get; set; }
    public int? MaxOutputBytes { get; set; }
    public long? MaxFileBytes { get; set; }
    public int? MaxJobs { get; set; }
    public int? JobRetentionSeconds { get; set; }
}

public sealed class UiConfigUpdate
{
    public UiTunnelSettings? Tunnel { get; set; }
    public UiExecSettings? Exec { get; set; }
}
