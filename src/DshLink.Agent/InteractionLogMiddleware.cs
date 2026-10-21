using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DshLink;

/// <summary>Records authenticated calls made by the DSH plugin to serial/ADB endpoints.</summary>
public sealed class InteractionLogMiddleware
{
    private const int MaxBodyBytes = 32 * 1024;
    private const int MaxPreview = 320;
    private readonly RequestDelegate _next;
    private readonly InteractionLog _log;

    public InteractionLogMiddleware(RequestDelegate next, InteractionLog log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var fromPlugin = context.Request.Headers.TryGetValue("X-Dsh-Board-Client", out var client) &&
                         client.ToString().Contains("plugin", StringComparison.OrdinalIgnoreCase);
        if (!fromPlugin || !IsDevicePath(path))
        {
            await _next(context);
            return;
        }

        var kind = path.StartsWith("/v1/serial", StringComparison.OrdinalIgnoreCase) ? "serial" : "adb";
        var tool = ToolName(path);
        var requestDetail = await DescribeRequestAsync(context, path);
        _log.Add(new InteractionEntry(DateTimeOffset.UtcNow, "→", kind, tool, requestDetail, null, 0));

        var stopwatch = Stopwatch.StartNew();
        var bufferResponse = !path.Contains("/screenshot", StringComparison.OrdinalIgnoreCase) &&
                             !path.Contains("/pull", StringComparison.OrdinalIgnoreCase);
        var originalBody = context.Response.Body;
        MemoryStream? memory = null;
        if (bufferResponse)
        {
            memory = new MemoryStream();
            context.Response.Body = memory;
        }

        var failed = false;
        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            var status = failed ? 500 : context.Response.StatusCode;
            string detail = "";
            if (memory is not null)
            {
                context.Response.Body = originalBody;
                memory.Position = 0;
                if (memory.Length > 0)
                {
                    using var reader = new StreamReader(memory, Encoding.UTF8, leaveOpen: true);
                    var text = await reader.ReadToEndAsync();
                    detail = SummarizeResponse(text);
                }
                memory.Position = 0;
                await memory.CopyToAsync(originalBody);
                await memory.DisposeAsync();
            }
            _log.Add(new InteractionEntry(DateTimeOffset.UtcNow, "←", kind, tool, detail, status, stopwatch.ElapsedMilliseconds));
        }
    }

    private static bool IsDevicePath(string path) =>
        path.StartsWith("/v1/serial", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/adb", StringComparison.OrdinalIgnoreCase);

    private static string ToolName(string path) => path.ToLowerInvariant() switch
    {
        "/v1/serial/ports" or "/v1/serial/sessions" => "board_serial_list",
        "/v1/serial/open" => "board_serial_open",
        "/v1/serial/read" => "board_serial_read",
        "/v1/serial/write" => "board_serial_write",
        "/v1/serial/close" => "board_serial_close",
        "/v1/serial/pulse" => "board_serial_pulse",
        "/v1/adb/status" => "board_adb_devices",
        "/v1/adb/shell" => "board_adb_shell",
        "/v1/adb/logcat" => "board_adb_logcat",
        "/v1/adb/screenshot" => "board_adb_screenshot",
        "/v1/adb/push" => "board_adb_push",
        "/v1/adb/pull" => "board_adb_pull",
        "/v1/adb/install" => "board_adb_install",
        "/v1/adb/reboot" => "board_adb_reboot",
        _ => path,
    };

    private static async Task<string> DescribeRequestAsync(HttpContext context, string path)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            return context.Request.QueryString.Value?.TrimStart('?') ?? "";

        if (path.Contains("/push", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/install", StringComparison.OrdinalIgnoreCase))
            return "<file payload omitted>";

        if (context.Request.ContentLength is null or > MaxBodyBytes) return "<body omitted>";

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        return Truncate(body);
    }

    private static string SummarizeResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Truncate(text);
            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                parts.Add("ports=" + string.Join(",", ports.EnumerateArray()
                    .Select(p => p.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(n => !string.IsNullOrEmpty(n))));
            if (doc.RootElement.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
                parts.Add("devices=" + string.Join(",", devices.EnumerateArray()
                    .Select(d =>
                    {
                        var serial = d.TryGetProperty("serial", out var s) ? s.GetString() : null;
                        var state = d.TryGetProperty("state", out var st) ? st.GetString() : null;
                        return string.IsNullOrEmpty(serial) ? null : $"{serial}={state}";
                    })
                    .Where(s => !string.IsNullOrEmpty(s))));
            foreach (var key in new[] { "error", "detail", "version", "bytes", "exitCode", "timedOut", "serial", "port", "sessionId", "text", "stdout", "stderr" })
            {
                if (!doc.RootElement.TryGetProperty(key, out var value)) continue;
                var rendered = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (!string.IsNullOrEmpty(rendered)) parts.Add($"{key}={Truncate(rendered, 220)}");
            }
            return parts.Count > 0 ? string.Join(" ", parts) : Truncate(text);
        }
        catch
        {
            return Truncate(text);
        }
    }

    private static string Truncate(string value, int max = MaxPreview)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var singleLine = value.Replace("\r", "\\r").Replace("\n", "\\n");
        return singleLine.Length <= max ? singleLine : singleLine[..max] + "…";
    }
}
