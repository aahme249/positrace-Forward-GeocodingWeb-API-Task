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
    /// Each address is normalised (apartment/unit qualifiers stripped) before querying Nominatim.
    /// If the normalised address yields no result, the service falls back to the postal code embedded
    /// in the input. The <c>strategy</c> field in each result indicates which path was taken:
    /// <c>address</c>, <c>postal_code</c>, <c>not_found</c>, or <c>error</c>.
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

public record GeocodeResponse(IEnumerable<GeocodeResult> Results);
