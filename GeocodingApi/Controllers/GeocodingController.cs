using GeocodingApi.Models;
using GeocodingApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace GeocodingApi.Controllers;

[ApiController]
[Route("api/v1/geocode")]
[Produces("application/json")]
public class GeocodingController(IGeocodingService geocodingService) : ControllerBase
{
    /// <summary>
    /// Forward-geocode a batch of Canadian street addresses.
    /// </summary>
    /// <remarks>
    /// **How it works**
    ///
    /// 1. Each address is **normalised** — unit/apt qualifiers (Apt, Unit, Suite, Room, #, dash-prefix) are stripped before querying Nominatim.
    /// 2. If the normalised address returns no result, the service **falls back to the postal code** embedded in the input.
    /// 3. Results are returned in the **same order** as the input. Every result echoes `originalAddress` so it maps unambiguously back to its source.
    /// 4. The `strategy` field tells you which path produced each result: `address`, `postal_code`, `not_found`, or `error`.
    ///
    /// **Performance**
    ///
    /// - Nominatim enforces **1 request/second**. Cold-cache batches complete in roughly N seconds for N unique addresses.
    /// - **Cached addresses return immediately** (SQLite lookup, no outbound call).
    /// - **Duplicate addresses** in the same batch or from concurrent clients result in a single Nominatim call shared by all waiters.
    ///
    /// **Timeout &amp; retry**
    ///
    /// Each Nominatim call times out after `Nominatim:TimeoutSeconds` (default 5 s) and is retried up to `Nominatim:RetryCount` times (default 3) with `Nominatim:RetryDelaySeconds` (default 2 s) between attempts. All values are configurable in `appsettings.json`.
    ///
    /// **Recommended batch size:** under 50 addresses for sub-minute response times.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(GeocodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Geocode([FromBody] GeocodeRequest request, CancellationToken ct)
    {
        if (request.Addresses.Count == 0)
            return BadRequest(new { error = "Addresses list must not be empty." });

        // One batch_request_id per incoming batch, shared by every address's log lines so the
        // whole batch can be traced with a single Loki query regardless of which address
        // or thread handled it.
        var batchRequestId = Guid.NewGuid().ToString("N")[..8];

        var tasks = request.Addresses.Select(addr => geocodingService.GeocodeAsync(addr, batchRequestId, ct));
        var results = await Task.WhenAll(tasks);

        return Ok(new GeocodeResponse(results));
    }
}

