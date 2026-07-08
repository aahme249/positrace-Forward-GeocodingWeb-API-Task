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

    // In-flight deduplication: key = normalized address, value = shared Task for the in-progress fetch
    private readonly ConcurrentDictionary<string, Task<CachedGeocode?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

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

        // In-flight deduplication: only one outbound call per unique address at any time.
        // The loop + TryAdd guarantees a single winner; all others await the winner's Task.
        while (true)
        {
            if (_inFlight.TryGetValue(normalizedAddress, out var existing))
            {
                _logger.LogDebug("[Thread {ThreadId}] Joining in-flight request for '{Address}'",
                    Environment.CurrentManagedThreadId, normalizedAddress);
                return await existing.WaitAsync(ct);
            }

            var tcs = new TaskCompletionSource<CachedGeocode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(normalizedAddress, tcs.Task))
                continue; // Another thread won the race; loop and await their task

            try
            {
                // Use CancellationToken.None so one caller's cancellation doesn't abort work
                // that concurrent callers are also waiting on.
                var result = await FetchAndCacheAsync(normalizedAddress, CancellationToken.None);
                tcs.SetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
                throw;
            }
            finally
            {
                _inFlight.TryRemove(normalizedAddress, out _);
            }
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
