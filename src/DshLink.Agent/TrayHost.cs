using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshLink;

/// <summary>Keeps the agent resident with a notification-area icon.</summary>
public static class TrayHost
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static TrayContext? _context;
    private static SynchronizationContext? _sync;

    public static void Start(string dashboardUrl, string token, string logFile, Action requestStop)
    {
        lock (Gate)
        {
            if (_thread is { IsAlive: true }) return;
            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    _context = new TrayContext(dashboardUrl, token, logFile, requestStop);
                    _sync = SynchronizationContext.Current;
                    ready.Set();
                    Application.Run(_context);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tray.log"),
                            $"{DateTimeOffset.Now:O} tray failed: {ex}\n");
                    }
                    catch { /* ignore */ }
                    ready.Set();
                }
                finally
                {
                    _context = null;
                    _sync = null;
                    _thread = null;
                }
            })
            {
                IsBackground = true,
                Name = "dsh-link-tray",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(3000);
        }
    }

    public static bool OpenDashboard(string url)
    {
        var sync = _sync;
        var context = _context;
        if (sync is null || context is null) return false;
        try
        {
            sync.Post(_ => context.ShowDashboard(url), null);
            return true;
        }
        catch { return false; }
    }

    public static bool OpenLog()
    {
        var sync = _sync;
        var context = _context;
        if (sync is null || context is null) return false;
        try
        {
            sync.Post(_ => context.ShowLog(), null);
            return true;
        }
        catch { return false; }
    }

    public static void Stop()
    {
        var sync = _sync;
        var context = _context;
        if (sync is null || context is null) return;
        try { sync.Post(_ => context.ExitThread(), null); }
        catch { /* ignore */ }
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly string _dashboardUrl;
    private readonly string _token;
    private readonly string _logFile;
    private readonly Action _requestStop;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private DashboardForm? _dashboard;
    private LogWindow? _logWindow;

    public TrayContext(string dashboardUrl, string token, string logFile, Action requestStop)
    {
        _dashboardUrl = dashboardUrl;
        _token = token;
        _logFile = logFile;
        _requestStop = requestStop;

        _menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("打开板端控制台");
        open.Click += (_, _) => OpenDashboard();
        _menu.Items.Add(open);

        var copy = new ToolStripMenuItem("复制 Agent token");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_token); } catch { /* ignore */ }
        };
        _menu.Items.Add(copy);

        _menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("退出服务");
        exit.Click += (_, _) => _requestStop();
        _menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Text = "DSH Link · Agent 运行中",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenDashboard();
        ShowTrayHintOnce();
    }

    private void ShowTrayHintOnce()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLink");
            var marker = Path.Combine(directory, "tray-hint-v1");
            if (File.Exists(marker)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
            var timer = new System.Windows.Forms.Timer { Interval = 1200 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                try
                {
                    _icon.ShowBalloonTip(5000, "DSH Link",
                        "Agent 正在后台运行。可在任务栏 ^ 中把 DSH Link 图标固定到托盘。",
                        ToolTipIcon.Info);
                }
                catch { /* ignore */ }
            };
            timer.Start();
        }
        catch { /* ignore */ }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch { /* ignore */ }
        return SystemIcons.Application;
    }

    private void OpenDashboard() => ShowDashboard(_dashboardUrl);

    public void ShowDashboard(string url)
    {
        if (_dashboard is { IsDisposed: false })
        {
            if (_dashboard.WindowState == FormWindowState.Minimized)
                _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.Activate();
            _dashboard.BringToFront();
            return;
        }
        _dashboard = new DashboardForm(url);
        _dashboard.FormClosed += (_, _) => _dashboard = null;
        _dashboard.Show();
        _dashboard.Activate();
    }

    public void ShowLog()
    {
        if (_logWindow is { IsDisposed: false })
        {
            if (_logWindow.WindowState == FormWindowState.Minimized)
                _logWindow.WindowState = FormWindowState.Normal;
            _logWindow.Activate();
            _logWindow.BringToFront();
            return;
        }
        _logWindow = new LogWindow(_logFile);
        _logWindow.FormClosed += (_, _) => _logWindow = null;
        _logWindow.Show();
        _logWindow.Activate();
    }

    protected override void ExitThreadCore()
    {
        try { _dashboard?.Close(); } catch { /* ignore */ }
        try { _logWindow?.Close(); } catch { /* ignore */ }
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class DashboardForm : Form
{
    private readonly string _url;
    private readonly WebView2 _webView;
    private readonly Label _status;

    public DashboardForm(string url)
    {
        _url = url;
        Text = "DSH Link 控制台";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 900);
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(13, 16, 21);
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath)) Icon = Icon.ExtractAssociatedIcon(processPath);
        }
        catch { /* default icon */ }

        _status = new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在加载控制台…",
            ForeColor = Color.White,
            BackColor = Color.FromArgb(13, 16, 21),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 12f),
        };
        _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        Controls.Add(_webView);
        Controls.Add(_status);
        Shown += async (_, _) => await InitializeWebViewAsync();
        FormClosed += (_, _) => { try { _webView.Dispose(); } catch { /* ignore */ } };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var suffix = Path.Combine("DshLink", "WebView2");
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), suffix);
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
                folder = Path.Combine(AppContext.BaseDirectory, ".webview2");
                Directory.CreateDirectory(folder);
            }

            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _status.Visible = false;
            _webView.Visible = true;
            _webView.Source = new Uri(_url);
            _webView.BringToFront();
        }
        catch (Exception ex)
        {
            _status.Text = "WebView2 初始化失败：\n" + ex.Message +
                           "\n\n可先使用 DSH Web 里的「板端控制台」。";
            _status.Visible = true;
        }
    }
}
