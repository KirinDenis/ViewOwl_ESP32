using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ViewOwl.Security.Models;
using ViewOwl.Security.Services;

namespace ViewOwl.Grabber.WebAPI.Configuration
{
    /// <summary>
    /// Configures .NET 8 built-in rate limiting policies for the ViewOwl API.
    /// </summary>
    public static class RateLimitingConfig
    {
        /// <summary>
        /// Rate-limiting policy name for authentication endpoints (POST /api/auth/login).
        /// Brute-force protection: 5 requests / 5 minutes per IP.
        /// </summary>
        public const string PolicyLogin = "login";

        /// <summary>
        /// Rate-limiting policy name for authenticated API routes (/api/**).
        /// General API protection: 60 requests / minute per IP.
        /// </summary>
        public const string PolicyApi = "api";

        /// <summary>
        /// Rate-limiting policy name for public static / landing page routes.
        /// Flood protection: 100 requests / minute per IP.
        /// </summary>
        public const string PolicyPublic = "public";

        /// <summary>
        /// Registers all rate-limiting policies on the DI builder.
        /// Must be called before <c>app.UseRateLimiter()</c>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same <paramref name="services"/> for chaining.</returns>
        public static IServiceCollection AddViewOwlRateLimiting(this IServiceCollection services)
        {
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                // ── Login policy — brute-force guard ──────────────────────────
                // Sliding window so a burst at the boundary of two fixed windows cannot bypass it.
                options.AddSlidingWindowLimiter(PolicyLogin, limiterOptions =>
                {
                    limiterOptions.Window             = TimeSpan.FromMinutes(5);
                    limiterOptions.SegmentsPerWindow  = 5;   // one segment per minute
                    limiterOptions.PermitLimit        = 5;
                    limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    limiterOptions.QueueLimit         = 0;  // no queuing — reject immediately
                });

                // ── API policy — general authenticated endpoints ───────────────
                options.AddFixedWindowLimiter(PolicyApi, limiterOptions =>
                {
                    limiterOptions.Window             = TimeSpan.FromMinutes(1);
                    limiterOptions.PermitLimit        = 60;
                    limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    limiterOptions.QueueLimit         = 0;
                });

                // ── Public policy — landing page / static files ───────────────
                options.AddFixedWindowLimiter(PolicyPublic, limiterOptions =>
                {
                    limiterOptions.Window             = TimeSpan.FromMinutes(1);
                    limiterOptions.PermitLimit        = 100;
                    limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                    limiterOptions.QueueLimit         = 0;
                });

                // ── Partition key: use the real IP stored by SecurityMiddleware ─
                // Policies above use the default per-IP key via the partition factory below.
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                {
                    string ip = ctx.Items.TryGetValue("RealIp", out object? stored) && stored is string s
                        ? s
                        : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    // No global limit — policies are applied per-endpoint via attributes.
                    return RateLimitPartition.GetNoLimiter(ip);
                });

                // ── Rejection handler ─────────────────────────────────────────
                options.OnRejected = async (context, ct) =>
                {
                    context.HttpContext.Response.StatusCode  = StatusCodes.Status429TooManyRequests;
                    context.HttpContext.Response.ContentType = "application/json";

                    // Log a BruteForce event for login-endpoint rejections.
                    string path = context.HttpContext.Request.Path.Value ?? string.Empty;
                    if (path.Contains("/api/auth/login", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var eventSvc = context.HttpContext.RequestServices
                                .GetService<ISecurityEventService>();
                            if (eventSvc is not null)
                            {
                                await eventSvc.LogAsync(
                                    context.HttpContext,
                                    SecurityEvent.EventBruteForce,
                                    "Rate limit exceeded on login endpoint")
                                    .ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // Never let logging failures propagate to the client.
                        }
                    }

                    await context.HttpContext.Response.WriteAsync(
                        """{"error":"Too many requests","retryAfter":60}""",
                        cancellationToken: ct)
                        .ConfigureAwait(false);
                };
            });

            return services;
        }
    }
}
