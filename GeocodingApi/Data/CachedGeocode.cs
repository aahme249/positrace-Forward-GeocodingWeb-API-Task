namespace GeocodingApi.Data;

public class CachedGeocode
{
    public int Id { get; set; }
    public required string NormalizedAddress { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public required string DisplayName { get; set; }
    public required string Strategy { get; set; }
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
}
