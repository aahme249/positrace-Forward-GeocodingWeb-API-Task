using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    // Metrics — one Meter per logical component; instruments are static so they survive DI rebuilds.
    private static readonly Meter _meter = new("GeocodingApi.Nominatim", "1.0");
    private static readonly Counter<long> _callCounter =
        _meter.CreateCounter<long>("nominatim.calls", description: "Total outbound Nominatim requests");
    private static readonly Histogram<double> _callDuration =
        _meter.CreateHistogram<double>("nominatim.call.duration", unit: "ms", description: "Nominatim HTTP round-trip duration");

    public NominatimClient(IHttpClientFactory httpFactory, ILogger<NominatimClient> logger, IConfiguration config)
    {
        _http   = httpFactory.CreateClient("nominatim");
        _logger = logger;

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
        return ThrottledGetAsync(url, path: "address", ct);
    }

    public Task<NominatimResult[]?> SearchByPostalCodeAsync(string postalCode, CancellationToken ct = default)
    {
        var url = $"search?postalcode={Uri.EscapeDataString(postalCode)}&countrycodes=ca&format=json&limit=1";
        return ThrottledGetAsync(url, path: "postal_code", ct);
    }

    private async Task<NominatimResult[]?> ThrottledGetAsync(string url, string path, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Nominatim rate limiter rejected the request.");

        _logger.LogDebug("Nominatim → {Url}", url);

        var sw = Stopwatch.StartNew();
        var outcome = "success";
        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<NominatimResult[]>(ct);
        }
        catch (HttpRequestException ex)
        {
            outcome = "error";
            _logger.LogError(ex, "Nominatim HTTP error for {Url}", url);
            throw;
        }
        finally
        {
            sw.Stop();
            _callCounter.Add(1, new TagList
            {
                { "path", path },
                { "outcome", outcome },
            });
            _callDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList
            {
                { "path", path },
                { "outcome", outcome },
            });
        }
    }

    public void Dispose() => _rateLimiter.Dispose();
}
