using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using GeocodingApi.Data;
using GeocodingApi.Models;
using Microsoft.EntityFrameworkCore;

namespace GeocodingApi.Services;

public interface IGeocodingService
{
    Task<GeocodeResult> GeocodeAsync(string rawAddress, string batchRequestId, CancellationToken ct = default);
}

public sealed class GeocodingService : IGeocodingService
{
    private const string Service = "GeocodingService";

    private readonly IAddressNormalizer _normalizer;
    private readonly INominatimClient _nominatim;
    private readonly IDbContextFactory<GeocodingDbContext> _dbFactory;
    private readonly ILogger<GeocodingService> _logger;

    // In-flight deduplication: key = normalized address, value = Lazy wrapping the shared in-progress Task.
    // GetOrAdd + Lazy<Task<T>> replaces the manual while(true)+TryAdd+TaskCompletionSource pattern:
    // GetOrAdd may construct competing Lazy instances under contention, but only one is stored and
    // ExecutionAndPublication guarantees exactly one factory invocation across all concurrent callers.
    // The tuple's RetryCount travels with the shared Task, so every waiter — not just the caller
    // that triggered the fetch — sees the same retry count for the REST response.
    private readonly ConcurrentDictionary<string, Lazy<Task<(CachedGeocode? Entry, int RetryCount)>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

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

    // Every log line is a fully-rendered logfmt string (not a message template) because the
    // Docker -> Promtail -> Loki pipeline only ever sees the rendered console text, not .NET's
    // structured properties — so the searchable fields have to live in the text itself. One
    // line, one event, every field present (nominatim_request_id uses "-" when no Nominatim
    // call was made for that event) so any single line is enough to filter on in Loki.
    private static string Fmt(string level, string batchRequestId, string? nominatimRequestId,
        string rawAddress, string normalizedAddress, string evt, string? extra = null)
    {
        var line = $"timestamp={DateTime.UtcNow:O} level={level} service={Service} " +
                   $"batch_request_id={batchRequestId} nominatim_request_id={nominatimRequestId ?? "-"} " +
                   $"thread_id={Environment.CurrentManagedThreadId} raw_address=\"{rawAddress}\" " +
                   $"normalized_address=\"{normalizedAddress}\" event={evt}";
        return extra is null ? line : $"{line} {extra}";
    }

    public async Task<GeocodeResult> GeocodeAsync(string rawAddress, string batchRequestId, CancellationToken ct = default)
    {
        var normalized = _normalizer.Normalize(rawAddress);
        var totalSw = Stopwatch.StartNew();

        _logger.LogInformation(Fmt("info", batchRequestId, null, rawAddress, normalized, "start"));

        try
        {
            var (cached, retryCount) = await GetOrFetchAsync(rawAddress, normalized, batchRequestId, ct);
            var strategy = cached?.Strategy ?? "not_found";

            totalSw.Stop();
            _requestCounter.Add(1, new TagList { { "strategy", strategy } });
            _requestDuration.Record(totalSw.Elapsed.TotalMilliseconds, new TagList { { "strategy", strategy } });

            _logger.LogInformation(Fmt("info", batchRequestId, null, rawAddress, normalized, "done",
                $"strategy={strategy} elapsed_ms={totalSw.Elapsed.TotalMilliseconds:F1}"));

            return cached is null
                ? new GeocodeResult { OriginalAddress = rawAddress, NormalizedAddress = normalized, Strategy = "not_found", RetryCount = retryCount }
                : new GeocodeResult
                {
                    OriginalAddress = rawAddress,
                    NormalizedAddress = normalized,
                    Latitude = cached.Latitude,
                    Longitude = cached.Longitude,
                    DisplayName = cached.DisplayName,
                    Strategy = cached.Strategy,
                    RetryCount = retryCount,
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            totalSw.Stop();
            _requestCounter.Add(1, new TagList { { "strategy", "error" } });
            _requestDuration.Record(totalSw.Elapsed.TotalMilliseconds, new TagList { { "strategy", "error" } });

            _logger.LogError(ex, Fmt("error", batchRequestId, null, rawAddress, normalized, "error",
                $"elapsed_ms={totalSw.Elapsed.TotalMilliseconds:F1} error=\"{ex.Message}\""));

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

    // RetryCount in the return tuple is null for a cache hit (no Nominatim call at all) and a
    // real count — possibly 0 — whenever at least one Nominatim call was made, whether by this
    // caller or by the in-flight fetch it joined.
    private async Task<(CachedGeocode? Entry, int? RetryCount)> GetOrFetchAsync(string rawAddress, string normalizedAddress, string batchRequestId, CancellationToken ct)
    {
        var hit = await ReadFromCacheAsync(normalizedAddress, ct);
        if (hit is not null)
        {
            _cacheCounter.Add(1, new TagList { { "outcome", "hit" } });
            _logger.LogDebug(Fmt("debug", batchRequestId, null, rawAddress, normalizedAddress, "cache_hit"));
            return (hit, null);
        }

        _cacheCounter.Add(1, new TagList { { "outcome", "miss" } });
        _logger.LogDebug(Fmt("debug", batchRequestId, null, rawAddress, normalizedAddress, "cache_miss"));

        var ourLazy = new Lazy<Task<(CachedGeocode? Entry, int RetryCount)>>(
            () => FetchEvictAndCache(rawAddress, normalizedAddress, batchRequestId),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _inFlight.GetOrAdd(normalizedAddress, ourLazy);

        // GetOrAdd returned someone else's Lazy — we're joining an in-flight fetch rather than
        // triggering a new Nominatim call ourselves.
        if (!ReferenceEquals(lazy, ourLazy))
        {
            _logger.LogDebug(Fmt("debug", batchRequestId, null, rawAddress, normalizedAddress, "dedup_join"));
        }

        var (entry, retryCount) = await lazy.Value.WaitAsync(ct);
        return (entry, retryCount);
    }

    private async Task<(CachedGeocode? Entry, int RetryCount)> FetchEvictAndCache(string rawAddress, string normalizedAddress, string batchRequestId)
    {
        try
        {
            return await FetchAndCacheAsync(rawAddress, normalizedAddress, batchRequestId, CancellationToken.None);
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

    private async Task<(CachedGeocode? Entry, int RetryCount)> FetchAndCacheAsync(string rawAddress, string normalizedAddress, string batchRequestId, CancellationToken ct)
    {
        // Fresh nominatim_request_id per outbound Nominatim call — the address search and the
        // postal-code fallback are separate HTTP requests (each with its own retry/circuit-breaker
        // run), so each gets traced independently. RetryCount accumulates across both calls: it's
        // "how many retries did it take to service this address", not just the last call's count.
        var addressReqId = Guid.NewGuid().ToString("N")[..8];
        var addressSearch = await _nominatim.SearchByAddressAsync(normalizedAddress, rawAddress, batchRequestId, addressReqId, ct);
        var results = addressSearch.Results;
        var strategy = "address";
        var nominatimRequestId = addressReqId;
        var retryCount = addressSearch.RetryCount;

        if (results is null || results.Length == 0)
        {
            var postalCode = _normalizer.ExtractPostalCode(normalizedAddress);
            if (postalCode is not null)
            {
                var postalReqId = Guid.NewGuid().ToString("N")[..8];
                nominatimRequestId = postalReqId;
                _logger.LogInformation(Fmt("info", batchRequestId, postalReqId, rawAddress, normalizedAddress, "postal_fallback",
                    $"postal_code=\"{postalCode}\""));
                var postalSearch = await _nominatim.SearchByPostalCodeAsync(postalCode, rawAddress, batchRequestId, postalReqId, ct);
                results = postalSearch.Results;
                strategy = "postal_code";
                // +1 for the fallback call itself (a second Nominatim request was made for this
                // address, regardless of whether it succeeded on its own first try) plus however
                // many times Polly had to retry that fallback call.
                retryCount += 1 + postalSearch.RetryCount;
            }
        }

        if (results is null || results.Length == 0)
        {
            _logger.LogInformation(Fmt("info", batchRequestId, nominatimRequestId, rawAddress, normalizedAddress, "nominatim_not_found"));
            return (null, retryCount);
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

        _logger.LogInformation(Fmt("info", batchRequestId, nominatimRequestId, rawAddress, normalizedAddress, "nominatim_found",
            $"strategy={strategy} lat={entry.Latitude} lon={entry.Longitude}"));
        await PersistAsync(rawAddress, normalizedAddress, batchRequestId, entry, ct);
        return (entry, retryCount);
    }

    private async Task PersistAsync(string rawAddress, string normalizedAddress, string batchRequestId, CachedGeocode entry, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.CachedGeocodes.Add(entry);
            await db.SaveChangesAsync(ct);
            _logger.LogDebug(Fmt("debug", batchRequestId, null, rawAddress, normalizedAddress, "persisted"));
        }
        catch (DbUpdateException)
        {
            _logger.LogDebug(Fmt("debug", batchRequestId, null, rawAddress, normalizedAddress, "duplicate"));
        }
    }
}
