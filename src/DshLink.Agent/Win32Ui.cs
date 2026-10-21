using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DshLink;

/// <summary>Small Win32 helpers so the GUI mode never leaves a console window behind.</summary>
public static class Win32Ui
{
    private const int SwHide = 0;
    private const int SwRestore = 9;
    private const uint MbIconError = 0x00000010;
    private const uint MbOk = 0x00000000;
    private const uint WmClose = 0x0010;
    private const uint WmSetIcon = 0x0080;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static Process? _edgeProcess;
    private static IntPtr _largeIcon;
    private static IntPtr _smallIcon;

    public static void HideConsole()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero) ShowWindow(handle, SwHide);
    }

    public static void Error(string text, string caption = "DSH Link")
    {
        MessageBox(IntPtr.Zero, text, caption, MbIconError | MbOk);
    }

    public static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (key?.GetValue(null) is string value && File.Exists(value)) return value;
        }
        catch { /* ignore */ }
        return null;
    }

    public static string? LastError { get; private set; }

    public static bool OpenAppWindow(string url)
    {
        LastError = null;
        if (_edgeProcess is { HasExited: false })
        {
            if (FocusAppWindow()) return true;
            LastError = "dashboard is already open but could not be focused; use the taskbar";
            return false;
        }
        if (FocusAppWindow()) return true;
        var edge = FindEdge();
        if (edge is null) { LastError = "Microsoft Edge not found"; return false; }

        // A unique profile per launch prevents the "close the browser to continue"
        // profile-lock prompt when an older app window is still alive.
        foreach (var profile in NewProfiles())
        {
            if (TryStartEdge(edge, profile, url, appMode: true)) return true;
        }
        return false;
    }

    private static string[] NewProfiles()
    {
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLink");
        CleanupOldProfiles(root);
        return new[]
        {
            Path.Combine(root, "EdgeApp-" + stamp),
            Path.Combine(AppContext.BaseDirectory, ".edge-profile-" + stamp),
        };
    }

    private static void CleanupOldProfiles(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root, "EdgeApp-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7))
                        Directory.Delete(directory, recursive: true);
                }
                catch { /* in use */ }
            }
        }
        catch { /* ignore */ }
    }

    public static bool FocusAppWindow()
    {
        try
        {
            var handles = FindAppWindows();
            Log($"focus check: windows={handles.Count}");
            if (handles.Count == 0) return false;
            var handle = handles[0];
            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            return true;
        }
        catch (Exception ex)
        {
            Log($"focus failed: {ex}");
            return false;
        }
    }

    private static List<IntPtr> FindAppWindows()
    {
        var found = new List<IntPtr>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var className = new StringBuilder(256);
                GetClassName(hWnd, className, className.Capacity);
                if (!className.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
                var length = GetWindowTextLength(hWnd);
                if (length <= 0) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(hWnd, text, text.Capacity);
                var title = text.ToString();
                if (title.Contains("DSH Link", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("dsh-link", StringComparison.OrdinalIgnoreCase))
                    found.Add(hWnd);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log($"enum windows failed: {ex}"); }
        return found;
    }

    public static void CloseAppWindow()
    {
        try
        {
            foreach (var handle in FindAppWindows())
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* ignore */ }

        var process = _edgeProcess;
        _edgeProcess = null;
        if (process is null) return;
        try
        {
            if (process.WaitForExit(2000)) return;
            process.Kill(entireProcessTree: true);
        }
        catch { /* already exited */ }
        finally { DestroyWindowIcons(); }
    }

    public static bool OpenBrowser(string url)
    {
        LastError = null;
        var edge = FindEdge();
        if (edge is not null)
        {
            foreach (var profile in NewProfiles())
            {
                if (TryStartEdge(edge, profile, url, appMode: false)) return true;
            }
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Log($"started default browser url={url}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            Log($"browser failed: {ex}");
            return false;
        }
    }

    private static bool TryStartEdge(string edge, string profile, string url, bool appMode)
    {
        try
        {
            Directory.CreateDirectory(profile);
            if (!Directory.Exists(profile)) throw new IOException($"cannot create Edge profile directory: {profile}");
            var startInfo = new ProcessStartInfo
            {
                FileName = edge,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            if (appMode) startInfo.ArgumentList.Add("--app=" + url);
            else
            {
                startInfo.ArgumentList.Add("--new-window");
                startInfo.ArgumentList.Add(url);
            }
            startInfo.ArgumentList.Add("--user-data-dir=" + profile);
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add("--disable-background-mode");
            // A fresh app profile otherwise shows Edge's sign-in/sync confirmation
            // ("account" popup). The dashboard needs no sync, extensions, or updates.
            startInfo.ArgumentList.Add("--disable-sync");
            startInfo.ArgumentList.Add("--disable-extensions");
            startInfo.ArgumentList.Add("--disable-component-update");
            startInfo.ArgumentList.Add("--disable-background-networking");
            startInfo.ArgumentList.Add("--no-service-autorun");
            startInfo.ArgumentList.Add("--disable-features=msEdgeSyncConfirmation,EdgeSyncConfirmation");
            startInfo.ArgumentList.Add("--window-size=1440,1000");
            var process = Process.Start(startInfo);
            _edgeProcess = process;
            if (appMode) _ = ApplyWindowIconAsync();
            Log($"started Edge pid={process?.Id} app={appMode} profile={profile} url={url}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            Log($"Edge start failed app={appMode} profile={profile}: {ex}");
            return false;
        }
    }

    private static async Task ApplyWindowIconAsync()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (executable is null) return;
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            if (ExtractIconEx(executable, 0, large, small, 1) == 0) return;
            _largeIcon = large[0];
            _smallIcon = small[0];

            // Edge paints its globe until the page commits; set the custom icon
            // again for a few seconds so title bar and taskbar both keep it.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                foreach (var handle in FindAppWindows())
                {
                    if (_largeIcon != IntPtr.Zero) SendMessage(handle, WmSetIcon, (IntPtr)1, _largeIcon);
                    if (_smallIcon != IntPtr.Zero) SendMessage(handle, WmSetIcon, (IntPtr)0, _smallIcon);
                }
                await Task.Delay(250);
            }
            Log("app window icon applied");
        }
        catch (Exception ex) { Log($"window icon failed: {ex}"); }
    }

    private static void DestroyWindowIcons()
    {
        if (_largeIcon != IntPtr.Zero) DestroyIcon(_largeIcon);
        if (_smallIcon != IntPtr.Zero) DestroyIcon(_smallIcon);
        _largeIcon = IntPtr.Zero;
        _smallIcon = IntPtr.Zero;
    }

    private static void Log(string text)
    {
        var line = $"{DateTimeOffset.Now:O} {text}\n";
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLink");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "gui.log"), line);
            return;
        }
        catch { /* fall back to the app directory */ }
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gui.log"), line); }
        catch { /* ignore */ }
    }
}
