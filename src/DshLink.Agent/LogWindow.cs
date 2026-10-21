using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace DshLink;

/// <summary>Dedicated live window for serial/ADB (and general agent) activity.</summary>
internal sealed class LogWindow : Form
{
    private readonly string _logFile;
    private readonly RichTextBox _output;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Button _pause;
    private readonly CheckBox _onlyDevices;
    private readonly TextBox _filter;
    private readonly CheckBox _autoScroll;
    private long _position;
    private bool _paused;

    public LogWindow(string logFile)
    {
        _logFile = logFile;
        Text = "DSH Link · 串口 / ADB 日志";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(760, 480);
        BackColor = Color.FromArgb(10, 13, 18);
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) Icon = Icon.ExtractAssociatedIcon(path);
        }
        catch { /* default */ }

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 4),
            BackColor = Color.FromArgb(18, 23, 31),
            WrapContents = false,
            AutoScroll = true,
        };
        _pause = new Button { Text = "暂停", Width = 68, Height = 28, FlatStyle = FlatStyle.Flat };
        _pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _pause.Text = _paused ? "继续" : "暂停";
        };
        var clear = new Button { Text = "清空", Width = 68, Height = 28, FlatStyle = FlatStyle.Flat };
        clear.Click += (_, _) => { _output?.Clear(); };
        _onlyDevices = new CheckBox
        {
            Text = "只看串口/ADB",
            Checked = true,
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(8, 5, 4, 0),
        };
        _filter = new TextBox { Width = 220, Height = 28, PlaceholderText = "过滤关键字（可空）" };
        _autoScroll = new CheckBox
        {
            Text = "自动滚动",
            Checked = true,
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(8, 5, 4, 0),
        };
        var folder = new Button { Text = "打开日志目录", Width = 105, Height = 28, FlatStyle = FlatStyle.Flat };
        folder.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{_logFile}\"", UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        toolbar.Controls.AddRange(new Control[] { _pause, clear, _onlyDevices, _filter, _autoScroll, folder });

        _output = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(9, 12, 17),
            ForeColor = Color.FromArgb(205, 220, 240),
            Font = new Font("Consolas", 9.5f),
            DetectUrls = false,
            WordWrap = false,
            HideSelection = false,
        };
        Controls.Add(_output);
        Controls.Add(toolbar);

        Load += (_, _) => LoadTail();
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    private void LoadTail()
    {
        try
        {
            if (!File.Exists(_logFile)) return;
            using var stream = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - 200 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            foreach (var line in text.Split('\n')) AppendLine(line.TrimEnd('\r'));
            _position = stream.Length;
        }
        catch (Exception ex)
        {
            AppendLine($"[log] 无法读取 {_logFile}: {ex.Message}");
        }
    }

    private void Poll()
    {
        if (_paused) return;
        try
        {
            if (!File.Exists(_logFile)) return;
            using var stream = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position) _position = 0;
            if (stream.Length == _position) return;
            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            _position = stream.Length;
            foreach (var line in text.Split('\n')) AppendLine(line.TrimEnd('\r'));
        }
        catch { /* file rotating; retry next tick */ }
    }

    private void AppendLine(string line)
    {
        if (line.Length == 0) return;
        if (_onlyDevices.Checked &&
            !line.Contains("serial", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("adb", StringComparison.OrdinalIgnoreCase)) return;
        if (_filter.Text.Length > 0 &&
            !line.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase)) return;

        var color = line.Contains("ERR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("WRN", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 145, 145)
            : Color.FromArgb(205, 220, 240);
        _output.SelectionStart = _output.TextLength;
        _output.SelectionLength = 0;
        _output.SelectionColor = color;
        _output.AppendText(line + Environment.NewLine);
        if (_output.TextLength > 500_000)
        {
            _output.Select(0, 100_000);
            _output.SelectedText = "";
        }
        if (_autoScroll.Checked)
        {
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        }
    }
}
