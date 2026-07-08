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
    /// Geocodes a list of Canadian street addresses.
    /// </summary>
    /// <remarks>
    /// <b>Normalisation:</b> unit/apartment qualifiers (Apt, Unit, Suite, Room, #, dash-prefix) are
    /// stripped before querying Nominatim so the geocoder sees the cleanest possible input.
    ///
    /// <b>Fallback:</b> if the normalised address returns no result, the service retries using only
    /// the postal code extracted from the input. The <c>strategy</c> field tells you which path
    /// produced the result: <c>address</c>, <c>postal_code</c>, <c>not_found</c>, or <c>error</c>.
    ///
    /// <b>Throughput:</b> Nominatim enforces 1 request/second. One new outbound call is fired every
    /// ≥1 s; calls run concurrently in-flight, so a batch of N cold-cache addresses completes in
    /// roughly N seconds (not N × HTTP_time). Cached addresses return immediately.
    ///
    /// <b>Timeout &amp; retry:</b> each Nominatim call times out after <c>Nominatim:TimeoutSeconds</c>
    /// (default 5 s) and is retried up to <c>Nominatim:RetryCount</c> times (default 3) with a
    /// fixed delay of <c>Nominatim:RetryDelaySeconds</c> (default 2 s) between attempts.
    ///
    /// <b>Batch sizing:</b> there is no hard limit on the number of addresses, but as a guideline:
    /// keep batches under 50 for sub-minute response times. For larger workloads consider splitting
    /// into multiple requests.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(GeocodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Geocode([FromBody] GeocodeRequest request, CancellationToken ct)
    {
        if (request.Addresses.Count == 0)
            return BadRequest(new { error = "Addresses list must not be empty." });

        var tasks = request.Addresses.Select(addr => geocodingService.GeocodeAsync(addr, ct));
        var results = await Task.WhenAll(tasks);

        return Ok(new GeocodeResponse(results));
    }
}

