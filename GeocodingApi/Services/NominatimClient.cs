using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GeocodingApi.Services;

public record NominatimResult(
    [property: JsonPropertyName("lat")] string Lat,
    [property: JsonPropertyName("lon")] string Lon,
    [property: JsonPropertyName("display_name")] string DisplayName
);

public interface INominatimClient
{
    Task<NominatimResult[]?> SearchByAddressAsync(string address, CancellationToken ct = default);
    Task<NominatimResult[]?> SearchByPostalCodeAsync(string postalCode, CancellationToken ct = default);
}

public sealed class NominatimClient : INominatimClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<NominatimClient> _logger;

    // Serialises all outbound calls and enforces ≥1 s between each
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastCallAt = DateTime.MinValue;

    public NominatimClient(IHttpClientFactory httpFactory, ILogger<NominatimClient> logger)
    {
        _http = httpFactory.CreateClient("nominatim");
        _logger = logger;
    }

    public Task<NominatimResult[]?> SearchByAddressAsync(string address, CancellationToken ct = default)
    {
        var url = $"search?q={Uri.EscapeDataString(address)}&countrycodes=ca&format=json&limit=1";
        return ThrottledGetAsync(url, ct);
    }

    public Task<NominatimResult[]?> SearchByPostalCodeAsync(string postalCode, CancellationToken ct = default)
    {
        var url = $"search?postalcode={Uri.EscapeDataString(postalCode)}&countrycodes=ca&format=json&limit=1";
        return ThrottledGetAsync(url, ct);
    }

    private async Task<NominatimResult[]?> ThrottledGetAsync(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastCallAt);
            if (wait > TimeSpan.Zero)
            {
                _logger.LogDebug("Nominatim rate-limit delay: {Ms}ms", (int)wait.TotalMilliseconds);
                await Task.Delay(wait, ct);
            }

            _logger.LogDebug("Nominatim → {Url}", url);
            _lastCallAt = DateTime.UtcNow;

            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<NominatimResult[]>(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Nominatim HTTP error for {Url}", url);
            throw;
        }
        finally
        {
            _throttle.Release();
        }
    }

    public void Dispose() => _throttle.Dispose();
}
