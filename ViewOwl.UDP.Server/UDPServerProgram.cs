using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using System.Globalization;
using System.Net;
using ViewOwl.Config;

namespace ViewOwl.UDP.Server
{
    /// <summary>
    /// Entry point for the ViewOwl UDP Server.
    /// Wires up DI, starts <see cref="UDPServer"/>, and loops printing live statistics.
    /// </summary>
    internal sealed class UDPListnet
    {
        private const int ListenPort = 11000;

        private static UDPServer? _server;

        static void Main()
        {
            ServiceCollection services = new ServiceCollection();
            ConfigureServices(services);

            ServiceProvider provider = services.BuildServiceProvider();
            _server = provider.GetRequiredService<UDPServer>();

            // Start HTTP API listener for internal management endpoints.
            var httpConfig = provider.GetRequiredService<IOptions<UdpServerHttpConfig>>().Value;
            var httpLogger = provider.GetRequiredService<ILogger<HttpApiListener>>();
            var httpListener = new HttpApiListener(_server, httpConfig, httpLogger);
            httpListener.Start();

            _server.StatusChanged += Server_StatusChanged;
            _server.Join          += Server_Join;
            _server.Done          += Server_Done;

            _server.Start();

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                httpListener.Dispose();
                _server.Dispose();
            };

            // Blocking status loop — the server runs until the process is killed.
            while (true)
            {
                OutStatuses();
                Thread.Sleep(1000);
            }
        }

        // ── DI configuration ──────────────────────────────────────────────────

        private static void ConfigureServices(IServiceCollection services)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs/server.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            services.AddLogging(cfg =>
            {
                cfg.ClearProviders();
                cfg.AddSerilog(Log.Logger);
                cfg.SetMinimumLevel(LogLevel.Information);
            });

            // ── Configuration ─────────────────────────────────────────────────

            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
                .Build();

            services.AddOptions();
            services.Configure<SharedConfig>(configuration.GetSection("Shared"));
            services.Configure<UdpServerHttpConfig>(configuration.GetSection("UdpServerHttp"));

            // Register concrete instances so dependencies can inject them directly if needed.
            services.AddSingleton<SharedConfig>(
                sp => sp.GetRequiredService<IOptions<SharedConfig>>().Value);
            services.AddSingleton<UdpServerHttpConfig>(
                sp => sp.GetRequiredService<IOptions<UdpServerHttpConfig>>().Value);

            // Single source of truth for supported display types — loads display-types.json
            // (deployed next to the binary) and answers per-display-type firmware lookups.
            services.AddSingleton<DisplayTypeCatalog>();

            // ── HTTP client for internal WebAPI calls ──────────────────────────

            string webApiBaseUrl =
                configuration["Shared:WebApiBaseUrl"] ?? "http://localhost:5000";

            services.AddSingleton<HttpClient>(_ =>
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(webApiBaseUrl),
                    // Loopback-only: all requests go to 127.0.0.1 so 2 s is ample.
                    // Keeping this low prevents a slow/blocked WebAPI from stalling
                    // the UDP ListenLoop (which calls GetAwaiter().GetResult()).
                    Timeout = TimeSpan.FromSeconds(2)
                };
                // SecurityMiddleware rejects requests with missing User-Agent (returns 400).
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ViewOwl.UDP.Server/1.0");
                return client;
            });

            services.AddSingleton<WebApiClient>();

            // ── UDP server ────────────────────────────────────────────────────

            services.AddSingleton(new IPEndPoint(IPAddress.Any, ListenPort));
            services.AddSingleton<UDPServer>();
        }

        // ── Status output ─────────────────────────────────────────────────────

        private static void OutStatuses()
        {
            OutStatus("Total clients",       _server?.Statuses?.TotalClients.ToString("N0", CultureInfo.InvariantCulture)       ?? string.Empty, 1,  2, ConsoleColor.White);
            OutStatus("Total AUTH",          _server?.Statuses?.TotalAuth.ToString("N0", CultureInfo.InvariantCulture)          ?? string.Empty, 1,  4, ConsoleColor.White);
            OutStatus("Max clients",         _server?.Statuses?.MaxClients.ToString("N0", CultureInfo.InvariantCulture)         ?? string.Empty, 1,  6, ConsoleColor.White);
            OutStatus("Current clients",     _server?.Statuses?.CurrentClients.ToString("N0", CultureInfo.InvariantCulture)     ?? string.Empty, 1,  8, ConsoleColor.White);
            OutStatus("Total MB sent",       (_server?.Statuses?.TotalKBSend / 1024 ?? 0).ToString("N0", CultureInfo.InvariantCulture),          1, 10, ConsoleColor.White);
            OutStatus("Total AUTH errors",   _server?.Statuses?.TotalAuthError.ToString("N0", CultureInfo.InvariantCulture)     ?? string.Empty, 1, 12, ConsoleColor.Red);
            OutStatus("Total wrong packets", _server?.Statuses?.TotalWrongPackets.ToString("N0", CultureInfo.InvariantCulture)  ?? string.Empty, 1, 14, ConsoleColor.Red);
        }

        private static void OutStatus(
            string title, string value, int x, int y,
            ConsoleColor foreground, ConsoleColor background = ConsoleColor.Black)
        {
            // Console output suppressed in server mode (no interactive terminal).
            return;
        }

        private static void Server_StatusChanged(object? sender, StatusChangedEventArgs e)
            => OutStatus("Status", e.Message, 1, 0, ConsoleColor.Yellow);

        private static void Server_Join(object? sender, StatusChangedEventArgs e)
        {
            if (e.EndPoint is { } ep)
                OutStatus(e.Message, $"{ep.Address}:{ep.Port}", 1, 16, ConsoleColor.White);
        }

        private static void Server_Done(object? sender, StatusChangedEventArgs e)
        {
            if (e.EndPoint is { } ep)
                OutStatus(e.Message, $"{ep.Address}:{ep.Port}", 1, 18, ConsoleColor.White);
        }
    }
}
