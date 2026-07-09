using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using GeocodingApi.Data;
using GeocodingApi.Models;
using Microsoft.EntityFrameworkCore;

namespace GeocodingApi.Services;

public interface IGeocodingService
{
    Task<GeocodeResult> GeocodeAsync(string rawAddress, CancellationToken ct = default);
}

public sealed class GeocodingService : IGeocodingService
{
    private readonly IAddressNormalizer _normalizer;
    private readonly INominatimClient _nominatim;
    private readonly IDbContextFactory<GeocodingDbContext> _dbFactory;
    private readonly ILogger<GeocodingService> _logger;

    // In-flight deduplication: key = normalized address, value = Lazy wrapping the shared in-progress Task.
    // GetOrAdd + Lazy<Task<T>> replaces the manual while(true)+TryAdd+TaskCompletionSource pattern:
    // GetOrAdd may construct competing Lazy instances under contention, but only one is stored and
    // ExecutionAndPublication guarantees exactly one factory invocation across all concurrent callers.
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedGeocode?>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Meter _meter = new("GeocodingApi.Geocoding", "1.0");
    private static readonly Counter<long> _requestCounter =
        _meter.CreateCounter<long>("geocode.requests.total", description: "Total geocode requests by outcome strategy");
    private static readonly Histogram<double> _requestDuration =
        _meter.CreateHistogram<double>("geocode.request.duration", unit: "ms", description: "End-to-end latency per address");
    private static readonly Counter<long> _cacheCounter =
        _meter.CreateCounter<long>("geocode.cache.lookups.total", description: "Cache hits and misses");

    public GeocodingService(
        IAddressNormalizer normalizer,
        INominatimClient nominatim,
        IDbContextFactory<GeocodingDbContext> dbFactory,
        ILogger<GeocodingService> logger)
    {
        _normalizer = normalizer;
        _nominatim = nominatim;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<GeocodeResult> GeocodeAsync(string rawAddress, CancellationToken ct = default)
    {
        var normalized = _normalizer.Normalize(rawAddress);

        // Short request_id — unique per address, propagated through every log line
        // in this async context so any single log is enough to pull the full trace.
        var requestId = Guid.NewGuid().ToString("N")[..8];
        var totalSw = Stopwatch.StartNew();

        _logger.LogInformation("[{RequestId}] [Thread {ThreadId}] START raw='{Raw}' normalized='{Normalized}'",
            requestId, Environment.CurrentManagedThreadId, rawAddress, normalized);

        try
        {
            var cached = await GetOrFetchAsync(normalized, requestId, ct);
            var strategy = cached?.Strategy ?? "not_found";

            totalSw.Stop();
            _requestCounter.Add(1, new TagList { { "strategy", strategy } });
            _requestDuration.Record(totalSw.Elapsed.TotalMilliseconds, new TagList { { "strategy", strategy } });

            _logger.LogInformation("[{RequestId}] [Thread {ThreadId}] DONE strategy={Strategy} elapsed_ms={ElapsedMs:F1}",
                requestId, Environment.CurrentManagedThreadId, strategy, totalSw.Elapsed.TotalMilliseconds);

            return cached is null
                ? new GeocodeResult { OriginalAddress = rawAddress, NormalizedAddress = normalized, Strategy = "not_found" }
                : new GeocodeResult
                {
                    OriginalAddress = rawAddress,
                    NormalizedAddress = normalized,
                    Latitude = cached.Latitude,
                    Longitude = cached.Longitude,
                    DisplayName = cached.DisplayName,
                    Strategy = cached.Strategy,
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            totalSw.Stop();
            _requestCounter.Add(1, new TagList { { "strategy", "error" } });
            _requestDuration.Record(totalSw.Elapsed.TotalMilliseconds, new TagList { { "strategy", "error" } });

            _logger.LogError(ex, "[{RequestId}] [Thread {ThreadId}] ERROR elapsed_ms={ElapsedMs:F1}",
                requestId, Environment.CurrentManagedThreadId, totalSw.Elapsed.TotalMilliseconds);

            return new GeocodeResult
            {
                OriginalAddress = rawAddress,
                NormalizedAddress = normalized,
                Strategy = "error",
                Error = ex.Message,
                RetryCount = (ex is NominatimException ne) ? ne.RetryCount : null,
            };
        }
    }

    private async Task<CachedGeocode?> GetOrFetchAsync(string normalizedAddress, string requestId, CancellationToken ct)
    {
        var hit = await ReadFromCacheAsync(normalizedAddress, ct);
        if (hit is not null)
        {
            _cacheCounter.Add(1, new TagList { { "outcome", "hit" } });
            _logger.LogDebug("[{RequestId}] [Thread {ThreadId}] cache:hit '{Address}'",
                requestId, Environment.CurrentManagedThreadId, normalizedAddress);
            return hit;
        }

        _cacheCounter.Add(1, new TagList { { "outcome", "miss" } });
        _logger.LogDebug("[{RequestId}] [Thread {ThreadId}] cache:miss '{Address}'",
            requestId, Environment.CurrentManagedThreadId, normalizedAddress);

        var lazy = _inFlight.GetOrAdd(normalizedAddress,
            _ => new Lazy<Task<CachedGeocode?>>(() => FetchEvictAndCache(normalizedAddress, requestId),
                 LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<CachedGeocode?> FetchEvictAndCache(string normalizedAddress, string requestId)
    {
        try
        {
            return await FetchAndCacheAsync(normalizedAddress, requestId, CancellationToken.None);
        }
        finally
        {
            _inFlight.TryRemove(normalizedAddress, out _);
        }
    }

    private async Task<CachedGeocode?> ReadFromCacheAsync(string normalizedAddress, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.CachedGeocodes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.NormalizedAddress == normalizedAddress, ct);
    }

    private async Task<CachedGeocode?> FetchAndCacheAsync(string normalizedAddress, string requestId, CancellationToken ct)
    {
        var results = await _nominatim.SearchByAddressAsync(normalizedAddress, ct);
        var strategy = "address";

        if (results is null || results.Length == 0)
        {
            var postalCode = _normalizer.ExtractPostalCode(normalizedAddress);
            if (postalCode is not null)
            {
                _logger.LogInformation("[{RequestId}] [Thread {ThreadId}] postal_code:fallback '{PostalCode}'",
                    requestId, Environment.CurrentManagedThreadId, postalCode);
                results = await _nominatim.SearchByPostalCodeAsync(postalCode, ct);
                strategy = "postal_code";
            }
        }

        if (results is null || results.Length == 0)
        {
            _logger.LogInformation("[{RequestId}] [Thread {ThreadId}] nominatim:not_found '{Address}'",
                requestId, Environment.CurrentManagedThreadId, normalizedAddress);
            return null;
        }

        var hit = results[0];
        var entry = new CachedGeocode
        {
            NormalizedAddress = normalizedAddress,
            Latitude = double.Parse(hit.Lat, System.Globalization.CultureInfo.InvariantCulture),
            Longitude = double.Parse(hit.Lon, System.Globalization.CultureInfo.InvariantCulture),
            DisplayName = hit.DisplayName,
            Strategy = strategy,
            CachedAt = DateTime.UtcNow,
        };

        _logger.LogInformation("[{RequestId}] [Thread {ThreadId}] nominatim:found strategy={Strategy} lat={Lat} lon={Lon}",
            requestId, Environment.CurrentManagedThreadId, strategy, entry.Latitude, entry.Longitude);
        await PersistAsync(normalizedAddress, requestId, entry, ct);
        return entry;
    }

    private async Task PersistAsync(string normalizedAddress, string requestId, CachedGeocode entry, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.CachedGeocodes.Add(entry);
            await db.SaveChangesAsync(ct);
            _logger.LogDebug("[{RequestId}] cache:persisted '{Address}'", requestId, normalizedAddress);
        }
        catch (DbUpdateException)
        {
            _logger.LogDebug("[{RequestId}] cache:duplicate (benign race)", requestId);
        }
    }
}
