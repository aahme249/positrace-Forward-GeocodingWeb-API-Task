using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;

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
    private readonly RateLimiter _rateLimiter;

    public NominatimClient(IHttpClientFactory httpFactory, ILogger<NominatimClient> logger, IConfiguration config)
    {
        _http   = httpFactory.CreateClient("nominatim");
        _logger = logger;

        // Configurable via Nominatim:RateLimitPerSecond (default 1 — Nominatim's public policy).
        // TokenBucket gives smooth spacing; FixedWindow would allow back-to-back calls at window edges.
        var rate = config.GetValue<int>("Nominatim:RateLimitPerSecond", 1);
        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit           = rate,
            TokensPerPeriod      = rate,
            ReplenishmentPeriod  = TimeSpan.FromSeconds(1),
            AutoReplenishment    = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit           = 10_000,
        });
        _logger.LogInformation("Nominatim rate limiter: {Rate} req/sec", rate);
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
        // Wait until the bucket has a token (≥1 s since the last was consumed).
        // AcquireAsync queues the caller if empty; the background timer refills
        // 1 token/sec independent of lease state, so N callers fire at ≥1 s intervals
        // and their HTTP calls run concurrently in-flight.
        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Nominatim rate limiter rejected the request.");

        _logger.LogDebug("Nominatim → {Url}", url);

        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<NominatimResult[]>(ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Nominatim HTTP error for {Url}", url);
            throw;
        }
    }

    public void Dispose() => _rateLimiter.Dispose();
}
