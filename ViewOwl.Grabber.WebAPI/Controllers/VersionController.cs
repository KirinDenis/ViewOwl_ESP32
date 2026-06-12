using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ViewOwl.Config;

namespace ViewOwl.Grabber.WebAPI.Controllers;

/// <summary>
/// Returns version information for the server and recommended firmware.
/// Public endpoint — no authentication required.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class VersionController : ControllerBase
{
    // Resolved once at startup — the assembly version never changes at runtime.
    private static readonly string ServerVersion =
        Assembly.GetEntryAssembly()
                ?.GetName()
                .Version
                ?.ToString(3)   // "major.minor.patch" — omits the trailing ".0" revision
        ?? "unknown";

    private readonly SharedConfig _shared;

    /// <summary>Initialises the controller with shared configuration.</summary>
    public VersionController(IOptions<SharedConfig> shared)
    {
        _shared = shared.Value;
    }

    /// <summary>
    /// Returns the running server version and the recommended firmware version.
    /// </summary>
    /// <remarks>
    /// Clients and devices can call this endpoint to verify compatibility before connecting.
    /// A firmware whose MAJOR or MINOR differs from the server's expectation will generate
    /// a warning in the server log.
    /// </remarks>
    /// <returns>Server version and recommended firmware version.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(VersionResponse), StatusCodes.Status200OK)]
    public IActionResult Get() =>
        Ok(new VersionResponse(ServerVersion, _shared.RecommendedFirmware));

    /// <summary>Version information returned by <c>GET /api/version</c>.</summary>
    /// <param name="Server">Running WebAPI server version (e.g. "1.2.0").</param>
    /// <param name="RecommendedFirmware">Firmware version tested with this server (e.g. "1.2.41").</param>
    public sealed record VersionResponse(string Server, string RecommendedFirmware);
}
