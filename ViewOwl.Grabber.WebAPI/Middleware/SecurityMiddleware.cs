using System.Net;
using ViewOwl.Security.Models;
using ViewOwl.Security.Services;

namespace ViewOwl.Grabber.WebAPI.Middleware
{
    /// <summary>
    /// Security guard middleware — runs on every request before routing.
    /// Responsibilities (in order):
    /// <list type="number">
    ///   <item>Extract the real client IP and store it in HttpContext.Items["RealIp"].</item>
    ///   <item>Reject requests from blocked IPs (HTTP 403).</item>
    ///   <item>Detect and log scanner probes to well-known vulnerability paths (HTTP 404).</item>
    ///   <item>Reject suspicious / empty User-Agent strings (HTTP 400).</item>
    ///   <item>Add hardened security response headers to every response.</item>
    ///   <item>Log 401/403 responses as InvalidToken and 429 as RateLimit events.</item>
    /// </list>
    /// </summary>
    public sealed class SecurityMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SecurityMiddleware> _logger;

        // Paths that vulnerability scanners commonly probe.
        private static readonly string[] ScannerPaths =
        [
            "/.env", "/.git", "/wp-admin", "/wp-login", "/phpmyadmin",
            "/admin.php", "/xmlrpc.php", "/.htaccess", "/config.php",
            "/backup", "/shell.php", "/actuator", "/console",
            "/.aws", "/cgi-bin"
        ];

        // Substrings found in well-known malicious or automated scanning tools.
        private static readonly string[] BlockedUaFragments =
        [
            "sqlmap", "nikto", "masscan", "zgrab", "nmap",
            "python-requests/2.", "dirbuster", "gobuster", "wfuzz",
            "nuclei", "hydra", "metasploit"
        ];

        /// <summary>
        /// Initialises the middleware with the next delegate and dependencies.
        /// </summary>
        /// <param name="next">The next middleware in the pipeline.</param>
        /// <param name="logger">Logger for diagnostic messages.</param>
        public SecurityMiddleware(RequestDelegate next, ILogger<SecurityMiddleware> logger)
        {
            _next   = next;
            _logger = logger;
        }

        /// <summary>
        /// Processes an HTTP request through the security checks.
        /// </summary>
        /// <param name="ctx">The current HTTP context.</param>
        /// <param name="events">Security event logging service (scoped, injected per-request).</param>
        /// <param name="blocks">IP block service (scoped, injected per-request).</param>
        public async Task InvokeAsync(
            HttpContext ctx,
            ISecurityEventService events,
            IIpBlockService blocks)
        {
            // ── Step 1: Extract real IP ───────────────────────────────────────
            string ip = ExtractIp(ctx);
            ctx.Items["RealIp"] = ip;

            // ── Loopback bypass ───────────────────────────────────────────────
            // Requests from 127.0.0.1 / ::1 are internal services (UDP Server,
            // health checks) that must never be blocked, rate-limited, or logged
            // as threats. Check the raw connection address — not the extracted IP —
            // to prevent X-Forwarded-For spoofing from the public internet.
            if (ctx.Connection.RemoteIpAddress is { } connAddr && IPAddress.IsLoopback(connAddr))
            {
                await _next(ctx).ConfigureAwait(false);
                return;
            }

            // ── Step 2: Check IP block list ───────────────────────────────────
            if (await blocks.IsBlockedAsync(ip).ConfigureAwait(false))
            {
                await events.LogAsync(ctx, SecurityEvent.EventBlocked, "IP on block list")
                    .ConfigureAwait(false);

                ctx.Response.StatusCode  = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "application/json";
                AddSecurityHeaders(ctx);
                await ctx.Response.WriteAsync(
                    """{"error":"Access denied","code":"IP_BLOCKED"}""")
                    .ConfigureAwait(false);
                return;
            }

            // ── Step 3: Detect scanner probes ─────────────────────────────────
            string lowerPath = ctx.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
            bool isScanner = Array.Exists(
                ScannerPaths,
                probe => lowerPath.StartsWith(probe, StringComparison.OrdinalIgnoreCase));

            if (isScanner)
            {
                await events.LogAsync(ctx, SecurityEvent.EventScannerProbe, lowerPath)
                    .ConfigureAwait(false);
                await blocks.AutoBlockIfNeededAsync(ip, events).ConfigureAwait(false);

                // Return 404, not 403 — do not reveal that probing is detected.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                AddSecurityHeaders(ctx);
                return;
            }

            // ── Step 4: Detect suspicious User-Agent strings ──────────────────
            string ua = ctx.Request.Headers.UserAgent.ToString();

            if (string.IsNullOrWhiteSpace(ua))
            {
                await events.LogAsync(ctx, SecurityEvent.EventSuspiciousActivity, "Empty User-Agent")
                    .ConfigureAwait(false);
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                AddSecurityHeaders(ctx);
                return;
            }

            string uaLower = ua.ToLowerInvariant();

            // Allow legitimate curl if it sends proper Accept headers.
            bool isCurlLegitimate = uaLower.StartsWith("curl/", StringComparison.Ordinal) &&
                                    ctx.Request.Headers.Accept.Count > 0;

            bool isSuspiciousUa = !isCurlLegitimate &&
                                  Array.Exists(
                                      BlockedUaFragments,
                                      frag => uaLower.Contains(frag, StringComparison.Ordinal));

            if (isSuspiciousUa)
            {
                await events.LogAsync(ctx, SecurityEvent.EventSuspiciousActivity,
                    $"Blocked UA: {ua[..Math.Min(ua.Length, 80)]}")
                    .ConfigureAwait(false);
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                AddSecurityHeaders(ctx);
                return;
            }

            // ── Step 5: Add security headers (applied to all passing responses) ─
            ctx.Response.OnStarting(() =>
            {
                AddSecurityHeaders(ctx);
                return Task.CompletedTask;
            });

            // ── Step 6: Pass to next middleware ───────────────────────────────
            await _next(ctx).ConfigureAwait(false);

            // ── Step 7: Post-response logging ─────────────────────────────────
            int status = ctx.Response.StatusCode;

            if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            {
                // Log for audit/metrics — but do NOT trigger auto-block here.
                // 401/403 responses fire during normal token refresh and page transitions;
                // counting them toward HighVolume caused legitimate users to be auto-blocked.
                await events.LogAsync(ctx, SecurityEvent.EventInvalidToken,
                    $"Response {status}")
                    .ConfigureAwait(false);
            }
            else if (status == StatusCodes.Status429TooManyRequests)
            {
                await events.LogAsync(ctx, SecurityEvent.EventRateLimit, "429 from rate limiter")
                    .ConfigureAwait(false);
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Extracts the originating client IP honouring reverse-proxy forwarding headers.
        /// </summary>
        private static string ExtractIp(HttpContext ctx)
        {
            string? forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                string first = forwarded.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first))
                    return first;
            }

            string? realIp = ctx.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(realIp))
                return realIp.Trim();

            return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Adds hardened HTTP security headers to the response.
        /// The privacy-policy Link header is also added here (GDPR / legal transparency).
        /// </summary>
        /// <remarks>
        /// X-Frame-Options is set to SAMEORIGIN (not DENY) so that /SiteTemplate responses
        /// can be embedded in iframes on the same origin — used by the index.html showcase.
        /// DENY would block all iframe embedding including the live widget preview cards.
        /// </remarks>
        private static void AddSecurityHeaders(HttpContext ctx)
        {
            IHeaderDictionary h = ctx.Response.Headers;
            h["X-Content-Type-Options"]  = "nosniff";
            h["X-Frame-Options"]         = "SAMEORIGIN";
            h["X-XSS-Protection"]        = "1; mode=block";
            h["Referrer-Policy"]         = "strict-origin-when-cross-origin";
            h["Permissions-Policy"]      = "geolocation=(), microphone=(), camera=()";
            h["Link"]                    = "</legal/privacy.html>; rel=\"privacy-policy\"";
        }
    }
}
