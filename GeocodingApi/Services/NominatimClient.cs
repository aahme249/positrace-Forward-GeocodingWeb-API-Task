using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Polly;

namespace GeocodingApi.Services;

public record NominatimResult(
    [property: JsonPropertyName("lat")] string Lat,
    [property: JsonPropertyName("lon")] string Lon,
    [property: JsonPropertyName("display_name")] string DisplayName
);

/// <summary>
/// A successful Nominatim call's results plus how many retries it took to get them — retries can
/// happen on the way to a success, not just on the way to a failure, so this has to travel with
/// the results themselves rather than only being attached to <see cref="NominatimException"/>.
/// </summary>
public sealed record NominatimSearchResult(NominatimResult[]? Results, int RetryCount);

/// <summary>
/// Thrown when all Nominatim retry attempts are exhausted or the circuit breaker is open.
/// Carries the number of retries that fired so callers can surface it in the response.
/// </summary>
public sealed class NominatimException(string message, int retryCount, Exception inner)
    : Exception(message, inner)
{
    public int RetryCount { get; } = retryCount;
}

public interface INominatimClient
{
    Task<NominatimSearchResult> SearchByAddressAsync(string address, string rawAddress, string batchRequestId, string nominatimRequestId, CancellationToken ct = default);
    Task<NominatimSearchResult> SearchByPostalCodeAsync(string postalCode, string rawAddress, string batchRequestId, string nominatimRequestId, CancellationToken ct = default);
}

public sealed class NominatimClient : INominatimClient, IDisposable
{
    private const string Service = "NominatimClient";

    // Per-call resilience-context properties. Set once in ThrottledGetAsync before the Polly
    // pipeline runs, then read back by the OnRetry callback (Program.cs) so retry log lines carry
    // the same trace fields as everything else, and by ThrottledGetAsync itself after the call
    // completes/fails to report the final retry_count.
    public static readonly ResiliencePropertyKey<int> RetryCountKey = new("nominatim.retry_count");
    public static readonly ResiliencePropertyKey<string> BatchRequestIdKey = new("nominatim.batch_request_id");
    public static readonly ResiliencePropertyKey<string> NominatimRequestIdKey = new("nominatim.nominatim_request_id");
    public static readonly ResiliencePropertyKey<string> RawAddressKey = new("nominatim.raw_address");
    public static readonly ResiliencePropertyKey<string> NormalizedAddressKey = new("nominatim.normalized_address");

    private readonly HttpClient _http;
    private readonly ILogger<NominatimClient> _logger;
    private readonly RateLimiter _rateLimiter;

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

    // Mirrors GeocodingService.Fmt — same field set and shape, service=NominatimClient, so a
    // grep/Loki query for a batch_request_id or nominatim_request_id returns matching lines from
    // both decoupled components in one consistent format. Internal (not private) so Program.cs's
    // OnRetry callback can build "retry" events in the same shape.
    internal static string Fmt(string level, string batchRequestId, string nominatimRequestId,
        string rawAddress, string normalizedAddress, string evt, string? extra = null)
    {
        var line = $"timestamp={DateTime.UtcNow:O} level={level} service={Service} " +
                   $"batch_request_id={batchRequestId} nominatim_request_id={nominatimRequestId} " +
                   $"thread_id={Environment.CurrentManagedThreadId} raw_address=\"{rawAddress}\" " +
                   $"normalized_address=\"{normalizedAddress}\" event={evt}";
        return extra is null ? line : $"{line} {extra}";
    }

    public Task<NominatimSearchResult> SearchByAddressAsync(string address, string rawAddress, string batchRequestId, string nominatimRequestId, CancellationToken ct = default)
    {
        var url = $"search?q={Uri.EscapeDataString(address)}&countrycodes=ca&format=json&limit=1";
        return ThrottledGetAsync(url, path: "address", rawAddress, address, batchRequestId, nominatimRequestId, ct);
    }

    public Task<NominatimSearchResult> SearchByPostalCodeAsync(string postalCode, string rawAddress, string batchRequestId, string nominatimRequestId, CancellationToken ct = default)
    {
        var url = $"search?postalcode={Uri.EscapeDataString(postalCode)}&countrycodes=ca&format=json&limit=1";
        return ThrottledGetAsync(url, path: "postal_code", rawAddress, postalCode, batchRequestId, nominatimRequestId, ct);
    }

    private async Task<NominatimSearchResult> ThrottledGetAsync(string url, string path, string rawAddress, string normalizedAddress,
        string batchRequestId, string nominatimRequestId, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Nominatim rate limiter rejected the request.");

        // Attach a ResilienceContext to the request so the OnRetry callback (Program.cs) can
        // increment RetryCountKey on each attempt and log with the same trace fields, and we can
        // read the final count back here after SendAsync returns or throws. Seeded to 0 so the
        // first attempt is explicitly retry_count=0 rather than relying on an unset default.
        var resilienceCtx = ResilienceContextPool.Shared.Get(ct);
        resilienceCtx.Properties.Set(RetryCountKey, 0);
        resilienceCtx.Properties.Set(BatchRequestIdKey, batchRequestId);
        resilienceCtx.Properties.Set(NominatimRequestIdKey, nominatimRequestId);
        resilienceCtx.Properties.Set(RawAddressKey, rawAddress);
        resilienceCtx.Properties.Set(NormalizedAddressKey, normalizedAddress);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.SetResilienceContext(resilienceCtx);

        _logger.LogDebug(Fmt("debug", batchRequestId, nominatimRequestId, rawAddress, normalizedAddress, "call_sent", $"url=\"{url}\" retry_count=0"));

        var sw = Stopwatch.StartNew();
        var outcome = "success";
        try
        {
            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            resilienceCtx.Properties.TryGetValue(RetryCountKey, out var finalRetries);
            _logger.LogDebug(Fmt("debug", batchRequestId, nominatimRequestId, rawAddress, normalizedAddress, "call_success", $"url=\"{url}\" retry_count={finalRetries}"));

            var results = await response.Content.ReadFromJsonAsync<NominatimResult[]>(ct);
            return new NominatimSearchResult(results, finalRetries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = "error";
            resilienceCtx.Properties.TryGetValue(RetryCountKey, out var retries);
            _logger.LogError(ex, Fmt("error", batchRequestId, nominatimRequestId, rawAddress, normalizedAddress, "call_error", $"url=\"{url}\" retry_count={retries} error=\"{ex.Message}\""));
            throw new NominatimException(ex.Message, retries, ex);
        }
        finally
        {
            sw.Stop();
            ResilienceContextPool.Shared.Return(resilienceCtx);
            _callCounter.Add(1, new TagList { { "path", path }, { "outcome", outcome } });
            _callDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "path", path }, { "outcome", outcome } });
        }
    }

    public void Dispose() => _rateLimiter.Dispose();
}
