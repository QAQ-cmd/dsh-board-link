using System.Security.Cryptography;
using System.Text;

namespace DshLink;

public sealed class AgentAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgentAuthMiddleware> _logger;
    private readonly byte[] _expected;

    public AgentAuthMiddleware(RequestDelegate next, AgentConfig config, ILogger<AgentAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _expected = Encoding.UTF8.GetBytes(config.Agent.Token);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/healthz", StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
            return;
        }

        // The dashboard shell is public; every /ui/api and /v1 call still requires the token.
        var path = context.Request.Path.Value ?? "";
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path is "/" or "/ui" or "/ui/" or "/favicon.ico"))
        {
            await _next(context);
            return;
        }

        var provided = ReadToken(context.Request);
        if (provided is null || !FixedTimeEquals(provided))
        {
            _logger.LogWarning("unauthorized request: {Method} {Path} from {Remote}",
                context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"unauthorized: send Authorization: Bearer <agent.token>\"}");
            return;
        }

        await _next(context);
    }

    private static string? ReadToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization[7..].Trim();
        var header = request.Headers["X-Dsh-Token"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }

    private bool FixedTimeEquals(string provided)
    {
        var actual = Encoding.UTF8.GetBytes(provided);
        return actual.Length == _expected.Length && CryptographicOperations.FixedTimeEquals(actual, _expected);
    }
}
