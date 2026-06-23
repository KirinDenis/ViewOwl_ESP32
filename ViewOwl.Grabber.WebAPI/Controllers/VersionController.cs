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
    private readonly DisplayTypeCatalog _displayTypeCatalog;

    /// <summary>Initialises the controller with shared configuration and the display-type registry.</summary>
    /// <param name="shared">Shared configuration — provides the global recommended firmware.</param>
    /// <param name="displayTypeCatalog">Registry of supported display types for per-type firmware lookups.</param>
    public VersionController(IOptions<SharedConfig> shared, DisplayTypeCatalog displayTypeCatalog)
    {
        _shared = shared.Value;
        _displayTypeCatalog = displayTypeCatalog;
    }

    /// <summary>
    /// Returns the running server version and the recommended firmware version.
    /// </summary>
    /// <remarks>
    /// Clients and devices can call this endpoint to verify compatibility before connecting.
    /// A firmware whose MAJOR or MINOR differs from the server's expectation will generate
    /// a warning in the server log. When <paramref name="displayType"/> is supplied and known,
    /// the response also carries that display type's recommended firmware.
    /// </remarks>
    /// <param name="displayType">Optional display type id (see <c>DisplayType</c>) to request per-type firmware.</param>
    /// <returns>Server version and recommended firmware version.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(VersionResponse), StatusCodes.Status200OK)]
    public IActionResult Get([FromQuery] byte? displayType = null)
    {
        string? displayTypeFirmware = null;
        if (displayType is { } id)
        {
            // Populate the per-type field only when the display type is known to the registry.
            displayTypeFirmware = _displayTypeCatalog.GetFirmwareTarget(id)?.RecommendedFirmware;
        }

        return Ok(new VersionResponse(
            ServerVersion,
            _shared.RecommendedFirmware,
            displayTypeFirmware));
    }

    /// <summary>Version information returned by <c>GET /api/version</c>.</summary>
    /// <param name="Server">Running WebAPI server version (e.g. "1.2.0").</param>
    /// <param name="RecommendedFirmware">Global firmware version tested with this server (e.g. "1.2.41").</param>
    /// <param name="DisplayTypeFirmware">
    /// Recommended firmware for the requested display type, or <c>null</c> when no display type was
    /// supplied or the supplied type is unknown.
    /// </param>
    public sealed record VersionResponse(
        string Server,
        string RecommendedFirmware,
        string? DisplayTypeFirmware = null);
}
