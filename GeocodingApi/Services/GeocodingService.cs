using System.Collections.Concurrent;
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
        _logger.LogInformation("[Thread {ThreadId}] Geocoding '{Raw}' → '{Normalized}'",
            Environment.CurrentManagedThreadId, rawAddress, normalized);

        try
        {
            var cached = await GetOrFetchAsync(normalized, ct);
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
            _logger.LogError(ex, "Geocoding failed for '{Address}'", normalized);
            return new GeocodeResult
            {
                OriginalAddress = rawAddress,
                NormalizedAddress = normalized,
                Strategy = "error",
                Error = ex.Message,
            };
        }
    }

    private async Task<CachedGeocode?> GetOrFetchAsync(string normalizedAddress, CancellationToken ct)
    {
        // Fast path: persistent cache hit (no locking needed for reads)
        var hit = await ReadFromCacheAsync(normalizedAddress, ct);
        if (hit is not null)
        {
            _logger.LogDebug("[Thread {ThreadId}] Cache hit for '{Address}'",
                Environment.CurrentManagedThreadId, normalizedAddress);
            return hit;
        }

        _logger.LogDebug("[Thread {ThreadId}] Cache miss — joining or starting fetch for '{Address}'",
            Environment.CurrentManagedThreadId, normalizedAddress);

        // GetOrAdd is not atomic, but the Lazy wrapper is: ExecutionAndPublication ensures the
        // factory runs exactly once even if two threads both construct a Lazy and race into GetOrAdd.
        // .WaitAsync(ct) lets each caller cancel their own wait without cancelling the shared fetch.
        var lazy = _inFlight.GetOrAdd(normalizedAddress,
            _ => new Lazy<Task<CachedGeocode?>>(() => FetchEvictAndCache(normalizedAddress),
                 LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<CachedGeocode?> FetchEvictAndCache(string normalizedAddress)
    {
        try
        {
            return await FetchAndCacheAsync(normalizedAddress, CancellationToken.None);
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

    private async Task<CachedGeocode?> FetchAndCacheAsync(string normalizedAddress, CancellationToken ct)
    {
        var results = await _nominatim.SearchByAddressAsync(normalizedAddress, ct);
        var strategy = "address";

        if (results is null || results.Length == 0)
        {
            var postalCode = _normalizer.ExtractPostalCode(normalizedAddress);
            if (postalCode is not null)
            {
                _logger.LogInformation("[Thread {ThreadId}] Address not found — falling back to postal code '{PostalCode}'",
                    Environment.CurrentManagedThreadId, postalCode);
                results = await _nominatim.SearchByPostalCodeAsync(postalCode, ct);
                strategy = "postal_code";
            }
        }

        if (results is null || results.Length == 0)
        {
            _logger.LogInformation("[Thread {ThreadId}] No results for '{Address}'",
                Environment.CurrentManagedThreadId, normalizedAddress);
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

        _logger.LogInformation("[Thread {ThreadId}] Found '{Address}' via {Strategy} → lat={Lat}, lon={Lon}",
            Environment.CurrentManagedThreadId, normalizedAddress, strategy, entry.Latitude, entry.Longitude);
        await PersistAsync(entry, ct);
        return entry;
    }

    private async Task PersistAsync(CachedGeocode entry, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.CachedGeocodes.Add(entry);
            await db.SaveChangesAsync(ct);
            _logger.LogDebug("Cached '{Address}' via {Strategy}", entry.NormalizedAddress, entry.Strategy);
        }
        catch (DbUpdateException)
        {
            // Benign race: another concurrent path (or a prior run) already persisted this entry.
            _logger.LogDebug("Cache entry for '{Address}' already exists; ignoring duplicate write", entry.NormalizedAddress);
        }
    }
}
