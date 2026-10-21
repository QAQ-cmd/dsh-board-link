using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DshLink;

/// <summary>
/// Minimal Windows serial backend built on the Win32 comm API. Using the native API avoids a
/// NuGet dependency and gives explicit control over read timeouts and modem lines.
/// </summary>
public sealed class WindowsSerialPort : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint PurgeRxClear = 0x0001;
    private const uint PurgeTxClear = 0x0002;
    private const uint PurgeRxAbort = 0x0004;
    private const uint PurgeTxAbort = 0x0008;
    private const uint ErrorTimeout = 121;
    private const uint ErrorOperationAborted = 995;
    private const uint ErrorIoPending = 997;
    private const uint EscapeSetRts = 3;
    private const uint EscapeClearRts = 4;
    private const uint EscapeSetDtr = 5;
    private const uint EscapeClearDtr = 6;
    private const uint MsCtsOn = 0x0010;
    private const uint MsDsrOn = 0x0020;
    private const uint MsRingOn = 0x0040;
    private const uint MsRlsdOn = 0x0080;

    private const uint FBinary = 0x0001;
    private const uint FParity = 0x0002;
    private const uint FOutxCtsFlow = 0x0004;
    private const uint FOutxDsrFlow = 0x0008;
    private const uint FDtrControlMask = 0x0030;
    private const uint FDtrEnable = 0x0010;
    private const uint FDsrSensitivity = 0x0040;
    private const uint FOutX = 0x0100;
    private const uint FInX = 0x0200;
    private const uint FRtsControlMask = 0x3000;
    private const uint FRtsEnable = 0x1000;
    private const uint FRtsHandshake = 0x2000;
    private const uint FAbortOnError = 0x4000;

    private readonly object _writeGate = new();
    private readonly object _stateGate = new();
    private SafeFileHandle? _handle;
    private CancellationTokenSource? _cts;
    private Thread? _reader;
    private volatile bool _disposed;
    private Action<byte[], int>? _onData;
    private Action<Exception>? _onFault;
    private long _bytesRead;
    private long _bytesWritten;

    public string PortName { get; }
    public int BaudRate { get; }
    public int DataBits { get; }
    public string Parity { get; }
    public string StopBits { get; }
    public string Handshake { get; }
    public bool Dtr { get; }
    public bool Rts { get; }
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public WindowsSerialPort(string portName, int baudRate = 115200, int dataBits = 8,
        string parity = "none", string stopBits = "one", string handshake = "none",
        bool dtr = true, bool rts = true)
    {
        PortName = NormalizePortName(portName);
        BaudRate = baudRate;
        DataBits = dataBits;
        Parity = (parity ?? "none").Trim().ToLowerInvariant();
        StopBits = (stopBits ?? "one").Trim().ToLowerInvariant();
        Handshake = (handshake ?? "none").Trim().ToLowerInvariant();
        Dtr = dtr;
        Rts = rts;
    }

    public void Open()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsSerialPort));
        var devicePath = @"\\.\" + PortName;
        var handle = CreateFile(devicePath, GenericRead | GenericWrite, 0, IntPtr.Zero,
            OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"cannot open {PortName}: {new Win32Exception(error).Message} (error {error})");
        }

        try
        {
            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            var def = $"baud={BaudRate} parity={ParityCode(Parity)} data={DataBits} stop={StopCode(StopBits)}";
            if (!BuildCommDCB(def, ref dcb))
                throw new IOException($"cannot build serial settings for {PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            var flags = dcb.Flags | FBinary;
            flags &= ~(FDtrControlMask | FRtsControlMask | FOutxCtsFlow | FOutxDsrFlow | FOutX | FInX | FDsrSensitivity | FAbortOnError);
            if (Parity != "none") flags |= FParity;
            if (Dtr) flags |= FDtrEnable;
            switch (Handshake)
            {
                case "none":
                    if (Rts) flags |= FRtsEnable;
                    break;
                case "hardware":
                    flags |= FOutxCtsFlow | FRtsHandshake;
                    break;
                case "xonxoff":
                case "software":
                    flags |= FOutX | FInX;
                    break;
                default:
                    throw new ArgumentException($"unsupported handshake '{Handshake}' (use none, hardware or xonxoff)");
            }
            dcb.Flags = flags;
            if (!SetCommState(handle, ref dcb))
                throw new IOException($"cannot configure {PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            SetupComm(handle, 64 * 1024, 64 * 1024);
            PurgeComm(handle, PurgeRxClear | PurgeTxClear | PurgeRxAbort | PurgeTxAbort);
            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 50,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 1000,
            };
            if (!SetCommTimeouts(handle, ref timeouts))
                throw new IOException($"cannot set timeouts for {PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            _handle = handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void StartReading(Action<byte[], int> onData, Action<Exception> onFault)
    {
        if (_handle is null) throw new InvalidOperationException("serial port is not open");
        _onData = onData;
        _onFault = onFault;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _reader = new Thread(() => ReadLoop(token))
        {
            IsBackground = true,
            Name = $"serial-read-{PortName}",
        };
        _reader.Start();
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new byte[4096];
        var handle = _handle;
        if (handle is null) return;
        while (!token.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (!ReadFile(handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero))
                {
                    var error = (uint)Marshal.GetLastWin32Error();
                    if (error is ErrorTimeout or ErrorOperationAborted or ErrorIoPending)
                    {
                        token.WaitHandle.WaitOne(10);
                        continue;
                    }
                    if (token.IsCancellationRequested) break;
                    throw new IOException($"serial read failed on {PortName}: {new Win32Exception((int)error).Message}");
                }
                if (read == 0)
                {
                    token.WaitHandle.WaitOne(10);
                    continue;
                }
                Interlocked.Add(ref _bytesRead, read);
                _onData?.Invoke(buffer, (int)read);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    try { _onFault?.Invoke(ex); } catch { /* ignore */ }
                }
                break;
            }
        }
    }

    public int Write(ReadOnlySpan<byte> data)
    {
        var handle = _handle;
        if (handle is null) throw new InvalidOperationException("serial port is not open");
        if (data.Length == 0) return 0;
        var total = 0;
        lock (_writeGate)
        {
            var buffer = data.ToArray();
            while (total < buffer.Length)
            {
                var chunk = Math.Min(4096, buffer.Length - total);
                var slice = new byte[chunk];
                Buffer.BlockCopy(buffer, total, slice, 0, chunk);
                if (!WriteFile(handle, slice, (uint)chunk, out var written, IntPtr.Zero))
                    throw new IOException($"serial write failed on {PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                if (written == 0) throw new IOException($"serial write returned 0 bytes on {PortName}");
                total += (int)written;
            }
        }
        Interlocked.Add(ref _bytesWritten, total);
        return total;
    }

    public void SetDtr(bool value)
    {
        var handle = _handle ?? throw new InvalidOperationException("serial port is not open");
        if (!EscapeCommFunction(handle, value ? EscapeSetDtr : EscapeClearDtr))
            throw new IOException($"cannot set DTR on {PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public void SetRts(bool value)
    {
        var handle = _handle ?? throw new InvalidOperationException("serial port is not open");
        if (!EscapeCommFunction(handle, value ? EscapeSetRts : EscapeClearRts))
            throw new IOException($"cannot set RTS on {PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public SerialModemStatus GetModemStatus()
    {
        var handle = _handle ?? throw new InvalidOperationException("serial port is not open");
        var status = new SerialModemStatus();
        if (GetCommModemStatus(handle, out var flags))
        {
            status.Cts = (flags & MsCtsOn) != 0;
            status.Dsr = (flags & MsDsrOn) != 0;
            status.Ring = (flags & MsRingOn) != 0;
            status.CarrierDetect = (flags & MsRlsdOn) != 0;
        }
        if (ClearCommError(handle, out var errors, out var stat))
        {
            status.InputQueue = stat.InputQueue;
            status.OutputQueue = stat.OutputQueue;
            status.FrameError = (errors & 0x0008) != 0;
            status.OverrunError = (errors & 0x0002) != 0;
            status.ParityError = (errors & 0x0004) != 0;
        }
        return status;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _handle?.Dispose(); } catch { /* ignore */ }
        try { _reader?.Join(1000); } catch { /* ignore */ }
        _cts?.Dispose();
        _handle = null;
    }

    public static IReadOnlyList<SerialPortInfo> ListPorts()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is string port && !string.IsNullOrWhiteSpace(port))
                    {
                        names.Add(port);
                        devices[port] = valueName;
                    }
                }
            }
        }
        catch
        {
            // Registry enumeration can be restricted; fall back to QueryDosDevice below.
        }

        if (names.Count == 0)
        {
            var buffer = new char[65536];
            var length = QueryDosDevice(null, buffer, buffer.Length);
            if (length > 0)
            {
                var text = new string(buffer, 0, length);
                foreach (var entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (entry.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(entry.AsSpan(3), out _))
                        names.Add(entry);
                }
            }
        }

        return names.Select(name => new SerialPortInfo(name, devices.GetValueOrDefault(name, ""), "serial"))
            .ToArray();
    }

    public static string NormalizePortName(string portName)
    {
        var value = (portName ?? "").Trim();
        if (value.StartsWith(@"\\.\", StringComparison.Ordinal)) value = value[4..];
        if (!value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || !int.TryParse(value.AsSpan(3), out var number) || number <= 0)
            throw new ArgumentException($"invalid Windows serial port name '{portName}' (expected COM1, COM3, ...)");
        return value.ToUpperInvariant();
    }

    private static string ParityCode(string parity) => parity switch
    {
        "none" or "n" => "N",
        "odd" or "o" => "O",
        "even" or "e" => "E",
        "mark" or "m" => "M",
        "space" or "s" => "S",
        _ => throw new ArgumentException($"unsupported parity '{parity}'"),
    };

    private static string StopCode(string stopBits) => stopBits switch
    {
        "one" or "1" => "1",
        "onepointfive" or "1.5" => "15",
        "two" or "2" => "2",
        _ => throw new ArgumentException($"unsupported stop bits '{stopBits}'"),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComStat
    {
        public uint Flags;
        public uint InputQueue;
        public uint OutputQueue;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool BuildCommDCB(string definition, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle file, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint bytesToRead,
        out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer,
        uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle file, uint function);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommModemStatus(SafeFileHandle file, out uint status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ClearCommError(SafeFileHandle file, out uint errors, out ComStat stat);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string? deviceName, char[] targetPath, int maxLength);
}

public sealed record SerialPortInfo(string Name, string Device, string Type);

public sealed class SerialModemStatus
{
    public bool Cts { get; set; }
    public bool Dsr { get; set; }
    public bool Ring { get; set; }
    public bool CarrierDetect { get; set; }
    public uint InputQueue { get; set; }
    public uint OutputQueue { get; set; }
    public bool FrameError { get; set; }
    public bool OverrunError { get; set; }
    public bool ParityError { get; set; }
}
