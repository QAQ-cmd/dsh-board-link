namespace DshLink;

public sealed class SerialOpenRequest
{
    public string Port { get; set; } = "";
    public int? BaudRate { get; set; }
    public int? DataBits { get; set; }
    public string? Parity { get; set; }
    public string? StopBits { get; set; }
    public string? Handshake { get; set; }
    public bool? Dtr { get; set; }
    public bool? Rts { get; set; }
}

public sealed class SerialReadRequest
{
    public string SessionId { get; set; } = "";
    public int? TimeoutMs { get; set; }
    public int? MaxBytes { get; set; }
    public bool? Peek { get; set; }
}

public sealed class SerialWriteRequest
{
    public string SessionId { get; set; } = "";
    public string? Text { get; set; }
    public string? Base64 { get; set; }
    public string? Newline { get; set; }
}

public sealed class SerialCloseRequest
{
    public string SessionId { get; set; } = "";
}

public sealed class SerialPulseRequest
{
    public string SessionId { get; set; } = "";
    public string Line { get; set; } = "dtr";
    public int? DurationMs { get; set; }
}

public sealed class AdbShellRequest
{
    public string? Serial { get; set; }
    public string Command { get; set; } = "";
    public int? TimeoutMs { get; set; }
}

public sealed class AdbLogcatRequest
{
    public string? Serial { get; set; }
    public int? Lines { get; set; }
    public string? Filter { get; set; }
    public string? Platform { get; set; }
    public int? TimeoutMs { get; set; }
}

public sealed class AdbScreenshotRequest
{
    public string? Serial { get; set; }
}

public sealed class AdbPushRequest
{
    public string? Serial { get; set; }
    public string RemotePath { get; set; } = "";
    public string DataBase64 { get; set; } = "";
}

public sealed class AdbPullRequest
{
    public string? Serial { get; set; }
    public string RemotePath { get; set; } = "";
    public long? MaxBytes { get; set; }
}

public sealed class AdbInstallRequest
{
    public string? Serial { get; set; }
    public string ApkBase64 { get; set; } = "";
}

public sealed class AdbRebootRequest
{
    public string? Serial { get; set; }
    public string? Mode { get; set; }
}

public sealed class TunnelActionRequest
{
    public bool? Enabled { get; set; }
}

public sealed class JobInputRequest
{
    public string? Text { get; set; }
    public bool? Close { get; set; }
}

public sealed class FileWriteRequest
{
    public string Path { get; set; } = "";
    public string DataBase64 { get; set; } = "";
    public bool? Overwrite { get; set; }
    public bool? CreateDirectories { get; set; }
}

public sealed class FileReadRequest
{
    public string Path { get; set; } = "";
    public long? MaxBytes { get; set; }
}
