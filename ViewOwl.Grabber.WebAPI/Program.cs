using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ViewOwl.Config;
using ViewOwl.Data;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories;
using ViewOwl.Data.Repositories.Interfaces;
using ViewOwl.Grabber.Config;
using ViewOwl.Grabber.Engine;
using ViewOwl.Grabber.Engine.Converters;
using ViewOwl.Grabber.WebAPI.BackgroundServices;
using ViewOwl.Grabber.WebAPI.Configuration;
using ViewOwl.Grabber.WebAPI.Hubs;
using ViewOwl.Grabber.WebAPI.DTOs;
using ViewOwl.Grabber.WebAPI.Middleware;
using ViewOwl.Grabber.WebAPI.Services;
using ViewOwl.Security;
using ViewOwl.Security.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────

builder.Configuration
    .AddJsonFile("appsettings.json",            optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.shared.json",     optional: true,  reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json",
                                                optional: true,  reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.Configure<GrabberConfig>(builder.Configuration.GetSection("Grabber"));
builder.Services.Configure<SharedConfig>(builder.Configuration.GetSection("Shared"));

// Display-type registry (display-types.json) — drives per-display-type firmware version checks.
builder.Services.AddSingleton<DisplayTypeCatalog>();

builder.Services.Configure<JwtConfig>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<AdminSeedConfig>(builder.Configuration.GetSection("AdminSeed"));
builder.Services.Configure<DatabaseConfig>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<DeviceMonitorConfig>(builder.Configuration.GetSection("DeviceMonitor"));

// ── CORS ─────────────────────────────────────────────────────────────────────

// Allow localhost origins for local development (Vite dev server or direct browser)
builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalDev", policy =>
        policy.WithOrigins(
                "http://localhost:5000",
                "http://localhost:5173",
                "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Main application database ─────────────────────────────────────────────────

string dbPath = builder.Configuration["Database:Path"] ?? "viewowl.db";
builder.Services.AddDbContext<ViewOwlDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Security database (separate SQLite file) ──────────────────────────────────

string securityDbPath = builder.Configuration["Security:DatabasePath"] ?? "security.db";
builder.Services.AddDbContext<SecurityDbContext>(options =>
    options.UseSqlite($"Data Source={securityDbPath}"));

// ── Security services ─────────────────────────────────────────────────────────

builder.Services.AddScoped<ISecurityEventService, SecurityEventService>();
builder.Services.AddScoped<IIpBlockService, IpBlockService>();
builder.Services.AddScoped<ISessionFingerprintService, SessionFingerprintService>();

// ── Repositories ─────────────────────────────────────────────────────────────

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
builder.Services.AddScoped<ITemplateRepository, TemplateRepository>();
builder.Services.AddScoped<IDevicePingRepository, DevicePingRepository>();
builder.Services.AddScoped<IGrabLogRepository, GrabLogRepository>();
builder.Services.AddScoped<IConnectionRepository, ConnectionRepository>();
builder.Services.AddScoped<IDeviceCommandRepository, DeviceCommandRepository>();
builder.Services.AddScoped<IFlowLayoutRepository, FlowLayoutRepository>();
builder.Services.AddScoped<IPipelineEventRepository, PipelineEventRepository>();
builder.Services.AddScoped<IGuestDeviceRepository, GuestDeviceRepository>();
builder.Services.AddScoped<ITemplateHealthService, TemplateHealthService>();

// ── JWT Authentication ────────────────────────────────────────────────────────

string jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"],
            ValidAudience            = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(
                                           Encoding.UTF8.GetBytes(jwtSecret))
        };
        // SignalR WebSocket upgrades cannot send an Authorization header;
        // the token is passed as ?access_token= in the query string instead.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ── Rate limiting (.NET 8 built-in) ──────────────────────────────────────────

// Policies: "login" (5 req/5 min), "api" (60 req/min), "public" (100 req/min)
// Partition key uses HttpContext.Items["RealIp"] set by SecurityMiddleware.
builder.Services.AddViewOwlRateLimiting();

// ── SignalR ───────────────────────────────────────────────────────────────────

// Use camelCase JSON so the JS client can access hub method arguments with
// standard camelCase property names (matching the rest of the API responses).
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase);

// DashboardNotificationService pushes real-time events to connected dashboard
// clients; singleton lifetime matches SignalR IHubContext<T> lifetime.
builder.Services.AddSingleton<IDashboardNotificationService, DashboardNotificationService>();

// FrameRefreshQueue is the in-memory bridge between GrabWorker (which knows when a new
// frame is ready) and InternalController (which the UDP server polls on every device PING).
// Singleton so both the background service and the controller share the same instance.
builder.Services.AddSingleton<IFrameRefreshQueue, FrameRefreshQueue>();

// FramePushThrottle enforces a per-template rate limit for browser-side frame pushes.
// Singleton so the ConcurrentDictionary of timestamps is shared across all requests.
builder.Services.AddSingleton<IFramePushThrottle, FramePushThrottle>();

// ── API ───────────────────────────────────────────────────────────────────────

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
// Swagger UI at /swagger (Development only).
// Click Authorize → paste the JWT from POST /api/auth/login (no 'Bearer' prefix needed).
string assemblyVersion = Assembly.GetEntryAssembly()
                                  ?.GetName().Version?.ToString(3) ?? "dev";
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "ViewOwl API",
        Version     = assemblyVersion,
        Description = "REST + SignalR API for the ViewOwl grab server. " +
                      "Authorize with the JWT from POST /api/auth/login.",
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = ParameterLocation.Header,
        Description  = "Paste the token from POST /api/auth/login — no 'Bearer' prefix needed.",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            []
        }
    });
    // Pin high-priority controllers (Auth) to the top of the Swagger UI list.
    c.DocumentFilter<SwaggerTagOrderFilter>();
});

// ── Grabber engine ────────────────────────────────────────────────────────────

// Chrome is the concrete singleton; registered under both interfaces so DI
// resolves the same instance whether a consumer asks for IGrabber or IScreenshotCapture.
builder.Services.AddSingleton<Chrome>();
builder.Services.AddSingleton<IGrabber>(sp => sp.GetRequiredService<Chrome>());
builder.Services.AddSingleton<IScreenshotCapture>(sp => sp.GetRequiredService<Chrome>());

// TabManager is a collaborator injected into Chrome
builder.Services.AddSingleton<TabManager>();

// ── Image converters ──────────────────────────────────────────────────────────

// All IImageConverter implementations are registered; Chrome resolves them as
// IEnumerable<IImageConverter> and selects by ScreenShotParamDTO.ConverterId.
builder.Services.AddSingleton<IImageConverter, BGR565Converter>();
builder.Services.AddSingleton<IImageConverter, MonochromeConverter>();

// ── Grab channel & workers ────────────────────────────────────────────────────

builder.Services.AddSingleton<GrabChannel>();

// GrabWorker: reads from GrabChannel and executes captures via IScreenshotCapture
builder.Services.AddHostedService<GrabWorker>();

// DeviceTemplateRefreshWorker: re-grabs DB templates (assigned to devices + IsPublic) every 5 min
// so clocks, weather widgets, and other dynamic content stay current automatically,
// and public landing-page templates always have a fresh frame ready for guest devices.
builder.Services.AddHostedService<DeviceTemplateRefreshWorker>();

// DeviceMonitorService: marks devices as offline when heartbeat is missing
builder.Services.AddHostedService<DeviceMonitorService>();

// DbCleanupWorker: daily purge of high-volume tables (DevicePings 7d, PipelineEvents 14d,
// GrabLogs 30d, SecurityEvents 30d) to keep SQLite file size bounded over time.
builder.Services.AddHostedService<DbCleanupWorker>();

// GuestDeviceRefreshWorker: re-grabs templates for landing-page guest devices every 5 min.
builder.Services.AddHostedService<GuestDeviceRefreshWorker>();

// ── Build ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

// ── EF migrations ─────────────────────────────────────────────────────────────

await using (var scope = app.Services.CreateAsyncScope())
{
    // Main application database
    var db = scope.ServiceProvider.GetRequiredService<ViewOwlDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);

    // Security database — separate file, separate migration history
    var secDb = scope.ServiceProvider.GetRequiredService<SecurityDbContext>();
    await secDb.Database.MigrateAsync().ConfigureAwait(false);

    // Seed the admin user only when the database is empty
    var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    if (!await userRepo.AnyAsync().ConfigureAwait(false))
    {
        var seedCfg = app.Configuration.GetSection("AdminSeed");
        string login    = seedCfg["Login"]    ?? "admin";
        // No fallback, and refuse to seed while the password is still the shipped
        // placeholder: an unconfigured or left-at-default value would silently
        // create a well-known admin account. The placeholder is marked with the
        // CHANGE_THIS prefix, so reject empty values and that prefix.
        string? configuredPassword = seedCfg["Password"];
        if (string.IsNullOrWhiteSpace(configuredPassword)
            || configuredPassword.StartsWith("CHANGE_THIS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AdminSeed:Password is not set (or still the default placeholder) in " +
                "appsettings.json. Set a strong password before starting the server.");
        }

        string password = configuredPassword;

        await userRepo.CreateAsync(new User
        {
            Login          = login,
            PasswordHash   = BCrypt.Net.BCrypt.HashPassword(password),
            Role           = UserRole.Admin,
            SandboxEnabled = false
        }).ConfigureAwait(false);

        app.Logger.LogInformation("Admin user '{Login}' seeded.", login);
    }

    // Seed lw-*.html / sf-*.html templates from SitesTemplates folder into the DB.
    // Idempotent — skips names that already exist. Runs on every startup safely.
    string exchangeFolder = app.Configuration["Shared:ExchangeFolder"] ?? string.Empty;
    string sitesTemplatesPath = Path.Combine(exchangeFolder, "SitesTemplates");
    if (Directory.Exists(sitesTemplatesPath))
    {
        await TemplateSeedService.SeedAsync(
            db, sitesTemplatesPath, app.Logger).ConfigureAwait(false);
    }
    else
    {
        app.Logger.LogWarning("TemplateSeed: SitesTemplates folder not found at {Path}.", sitesTemplatesPath);
    }
}

// ── Middleware pipeline ────────────────────────────────────────────────────────
// Order matters: ForwardedHeaders → Security → RateLimit → Routing → Auth → Controllers

// Trust X-Forwarded-For / X-Forwarded-Proto from Caddy (same-host reverse proxy).
// Clears default network/proxy trust lists and allows only loopback (127.0.0.1)
// so external callers cannot spoof these headers.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
forwardedOptions.KnownProxies.Add(IPAddress.Loopback);
app.UseForwardedHeaders(forwardedOptions);

app.UseCors("LocalDev");

// SecurityMiddleware runs first: IP extraction, block checks, scanner detection,
// UA filtering, and security header injection all happen here.
app.UseMiddleware<SecurityMiddleware>();

// Rate limiting uses HttpContext.Items["RealIp"] set by SecurityMiddleware above.
app.UseRateLimiter();

// Serve static files from wwwroot (login page, dashboard, JS, vendor assets)
// HTML files get no-cache so browsers always re-validate after a deploy;
// Vite already appends content hashes to JS/CSS filenames so they can be
// cached aggressively — the default MaxAge handles that automatically.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || ctx.File.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"]        = "no-cache";
            ctx.Context.Response.Headers["Expires"]       = "0";
        }
    }
});

app.UseAuthentication();

// Log API requests (method, path, user, status, duration)
app.UseApiLogging();

// Block external callers from reaching /api/internal/* — localhost only
app.UseLocalhostOnly();

app.UseAuthorization();

app.MapControllers();
app.MapHub<ViewOwlHub>("/hubs/viewowl");
app.MapHub<DashboardHub>("/hubs/dashboard");

// React dashboard SPA — serve index.html for any unmatched /dashboard/* path
app.MapFallbackToFile("/dashboard/{*path:nonfile}", "/dashboard/index.html");

// Root serves the public landing page — dashboard is at /dashboard
app.MapGet("/", () => Results.Redirect("/index.html"));

// Bootstrap admin panel lives at wwwroot/dashboard.html.
// /admin and /admin/ redirect there so operators have a memorable URL.
app.MapGet("/admin",  () => Results.Redirect("/dashboard.html")).ExcludeFromDescription();
app.MapGet("/admin/", () => Results.Redirect("/dashboard.html")).ExcludeFromDescription();

// Swagger is enabled in Development only — never exposed on the production server.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", $"ViewOwl API v{assemblyVersion}");
        // Skip the first "Try it out" click — Execute is the only button needed.
        c.EnableTryItOutByDefault();
        // Filter box at the top — type any path fragment to narrow the list instantly.
        c.EnableFilter();
        // All operations collapsed on load — clean list, expand only what you need.
        c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
        // Hide the Schemas section at the bottom — rarely useful during API exploration.
        c.DefaultModelsExpandDepth(-1);
        // Show request duration next to the response — handy for spotting slow endpoints.
        c.DisplayRequestDuration();
        // Persist the Bearer token across page refreshes (localStorage) — dev convenience only.
        c.EnablePersistAuthorization();
    });
}

// ── Grabber initialisation ─────────────────────────────────────────────────────

// InitAsync must run AFTER the HTTP server starts listening so Chrome can reach
// SiteTemplateController at http://localhost:5000 during the warm-up page load.
// ApplicationStarted fires once Kestrel is fully bound and accepting requests.
var grabber = app.Services.GetRequiredService<IGrabber>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            await grabber.InitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Grabber initialisation failed.");
        }
    });
});

await app.RunAsync().ConfigureAwait(true);
