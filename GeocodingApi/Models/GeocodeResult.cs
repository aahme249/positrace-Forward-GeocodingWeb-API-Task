namespace GeocodingApi.Models;

public record GeocodeResponse(IEnumerable<GeocodeResult> Results);

public record GeocodeResult
{
    public required string OriginalAddress { get; init; }
    public required string NormalizedAddress { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>"address" | "postal_code" | "not_found" | "error"</summary>
    public required string Strategy { get; init; }

    public bool Found => Latitude.HasValue;
    public string? Error { get; init; }

    /// <summary>
    /// Total number of extra Nominatim requests beyond the first — Polly retries on either call,
    /// plus 1 if the postal-code fallback was attempted at all (a second request, whether or not
    /// it needed its own retries). Populated for every strategy where at least one Nominatim call
    /// was made ("address", "postal_code", "not_found", "error"); null only for a cache hit, where
    /// no Nominatim call happened.
    /// </summary>
    public int? RetryCount { get; init; }
}
