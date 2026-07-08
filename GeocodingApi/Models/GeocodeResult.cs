namespace GeocodingApi.Models;

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
}
