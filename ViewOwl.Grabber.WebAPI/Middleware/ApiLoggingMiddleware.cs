using System.Diagnostics;
using System.Security.Claims;

namespace ViewOwl.Grabber.WebAPI.Middleware
{
    /// <summary>
    /// Logs method, path, user, status code, and elapsed time for every <c>/api/*</c> request.
    /// Non-API paths (static files, hubs) are passed through without logging.
    /// </summary>
    public sealed class ApiLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ApiLoggingMiddleware> _logger;

        /// <summary>Initialises the middleware.</summary>
        public ApiLoggingMiddleware(RequestDelegate next, ILogger<ApiLoggingMiddleware> logger)
        {
            _next   = next;
            _logger = logger;
        }

        /// <summary>Processes the HTTP context.</summary>
        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            string method = context.Request.Method;
            string path   = context.Request.Path;
            string userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";

            var sw = Stopwatch.StartNew();
            await _next(context).ConfigureAwait(false);
            sw.Stop();

            _logger.LogInformation(
                "API {Method} {Path} by user {UserId} → {StatusCode} in {ElapsedMs}ms",
                method,
                path,
                userId,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Extension methods for registering <see cref="ApiLoggingMiddleware"/>.</summary>
    public static class ApiLoggingMiddlewareExtensions
    {
        /// <summary>Adds <see cref="ApiLoggingMiddleware"/> to the pipeline.</summary>
        public static IApplicationBuilder UseApiLogging(this IApplicationBuilder builder)
            => builder.UseMiddleware<ApiLoggingMiddleware>();
    }
}
