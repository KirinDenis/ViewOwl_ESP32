using System.Net;

namespace ViewOwl.Grabber.WebAPI.Middleware
{
    /// <summary>
    /// Rejects requests to <c>/api/internal/*</c> from any IP other than localhost.
    /// Prevents external callers from reaching endpoints that are intended only
    /// for the co-located UDP Server process.
    /// </summary>
    public sealed class LocalhostOnlyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<LocalhostOnlyMiddleware> _logger;

        /// <summary>
        /// Initialises the middleware.
        /// </summary>
        /// <param name="next">Next middleware delegate.</param>
        /// <param name="logger">Logger.</param>
        public LocalhostOnlyMiddleware(RequestDelegate next, ILogger<LocalhostOnlyMiddleware> logger)
        {
            _next   = next;
            _logger = logger;
        }

        /// <summary>
        /// Processes the request. Blocks non-localhost callers on <c>/api/internal</c> paths.
        /// </summary>
        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path.StartsWithSegments("/api/internal", StringComparison.OrdinalIgnoreCase))
            {
                IPAddress? remote = context.Connection.RemoteIpAddress;

                // Normalise IPv4-mapped IPv6 (::ffff:127.0.0.1) to plain IPv4
                if (remote is not null && remote.IsIPv4MappedToIPv6)
                    remote = remote.MapToIPv4();

                bool isLocalhost = remote is not null &&
                    (remote.Equals(IPAddress.Loopback) ||      // 127.0.0.1
                     remote.Equals(IPAddress.IPv6Loopback));   // ::1

                if (!isLocalhost)
                {
                    _logger.LogWarning(
                        "Blocked external request to internal API from {IP} ({Path})",
                        remote,
                        context.Request.Path);

                    context.Response.StatusCode  = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Forbidden","detail":"Internal API is accessible from localhost only."}""")
                        .ConfigureAwait(false);
                    return;
                }
            }

            await _next(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Extension method for registering <see cref="LocalhostOnlyMiddleware"/>.
    /// </summary>
    public static class LocalhostOnlyMiddlewareExtensions
    {
        /// <summary>
        /// Adds <see cref="LocalhostOnlyMiddleware"/> to the pipeline.
        /// Call this before <c>UseAuthorization()</c>.
        /// </summary>
        public static IApplicationBuilder UseLocalhostOnly(this IApplicationBuilder app)
            => app.UseMiddleware<LocalhostOnlyMiddleware>();
    }
}
