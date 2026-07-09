using System.Collections.Concurrent;
using GeocodingApi.Data;
using GeocodingApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace GeocodingApi.Tests;

public sealed class GeocodingServiceTests : IDisposable
{
    private const string RequestId = "test-req";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<GeocodingDbContext> _dbOptions;
    private readonly INominatimClient _nominatim;
    private readonly CapturingLogger<GeocodingService> _logs;
    private readonly GeocodingService _sut;

    public GeocodingServiceTests()
    {
        // Shared in-memory SQLite — connection must stay open for the schema to persist across factory calls
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<GeocodingDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var seed = new GeocodingDbContext(_dbOptions);
        seed.Database.EnsureCreated();

        _nominatim = Substitute.For<INominatimClient>();
        _logs = new CapturingLogger<GeocodingService>();
        _sut = new GeocodingService(
            new AddressNormalizer(),
            _nominatim,
            new FixedConnectionFactory(_dbOptions),
            _logs);
    }

    public void Dispose() => _connection.Dispose();

    // --- happy path ---

    [Fact]
    public async Task GeocodeAsync_ReturnsAddressStrategy_OnNominatimHit()
    {
        GivenNominatimReturns("45.4215", "-75.6972", "Ottawa");

        var result = await _sut.GeocodeAsync("100 Wellington St, Ottawa, ON K1A 0A6", RequestId);

        Assert.Equal("address", result.Strategy);
        Assert.True(result.Found);
        Assert.Equal(45.4215, result.Latitude);
    }

    [Fact]
    public async Task GeocodeAsync_NormalizesAddressBeforeLookup()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        await _sut.GeocodeAsync("Unit 5 100 King St W, Toronto, ON M5X 1A9", RequestId);

        // Nominatim must receive the stripped address, not the raw input
        await _nominatim.Received(1)
            .SearchByAddressAsync("100 King St W, Toronto, ON M5X 1A9", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeocodeAsync_FallsBackToPostalCode_WhenAddressNotFound()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(Array.Empty<NominatimResult>(), RetryCount: 0)));
        _nominatim.SearchByPostalCodeAsync("M5V3A8", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(
                new[] { new NominatimResult("43.6426", "-79.3871", "M5V, Toronto") }, RetryCount: 0)));

        var result = await _sut.GeocodeAsync("99999 Nowhere Blvd, Toronto, ON M5V 3A8", RequestId);

        Assert.Equal("postal_code", result.Strategy);
        Assert.True(result.Found);
        // The fallback is a second Nominatim request even though it succeeded on its own first
        // try — retryCount counts total extra requests, not just each call's own Polly retries.
        Assert.Equal(1, result.RetryCount);
    }

    [Fact]
    public async Task GeocodeAsync_ReturnsNotFound_WhenBothSearchesFail()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(Array.Empty<NominatimResult>(), RetryCount: 0)));

        var result = await _sut.GeocodeAsync("Unit 5 99999 Nowhere Blvd, Faketown, ON", RequestId);

        Assert.Equal("not_found", result.Strategy);
        Assert.False(result.Found);
        Assert.Null(result.Latitude);
    }

    [Fact]
    public async Task GeocodeAsync_ReturnsNotFound_WithRetryCountOne_WhenPostalFallbackAlsoAttempted()
    {
        // Both calls succeed (no Polly retries) but come back empty — the postal fallback is
        // still a second Nominatim request, so retryCount should be 1, not 0.
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(Array.Empty<NominatimResult>(), RetryCount: 0)));
        _nominatim.SearchByPostalCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(Array.Empty<NominatimResult>(), RetryCount: 0)));

        var result = await _sut.GeocodeAsync("123-12 Main St, Toronto, ON M5V 2T6", RequestId);

        Assert.Equal("not_found", result.Strategy);
        Assert.False(result.Found);
        Assert.Equal(1, result.RetryCount);
    }

    // --- retry count surfacing ---

    [Fact]
    public async Task GeocodeAsync_ReturnsZeroRetryCount_WhenFoundOnFirstAttempt()
    {
        GivenNominatimReturns("45.4215", "-75.6972", "Ottawa", retryCount: 0);

        var result = await _sut.GeocodeAsync("100 Wellington St, Ottawa, ON K1A 0A6", RequestId);

        Assert.Equal(0, result.RetryCount);
    }

    [Fact]
    public async Task GeocodeAsync_ReturnsRetryCount_WhenFoundAfterRetries()
    {
        // A call that ultimately succeeds after Polly retried it twice must still surface that
        // retry count in the response — not just null, which is what a caller would otherwise
        // read as "no retries happened".
        GivenNominatimReturns("45.4215", "-75.6972", "Ottawa", retryCount: 2);

        var result = await _sut.GeocodeAsync("100 Wellington St, Ottawa, ON K1A 0A6", RequestId);

        Assert.Equal(2, result.RetryCount);
    }

    [Fact]
    public async Task GeocodeAsync_ReturnsNullRetryCount_OnCacheHit()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto", retryCount: 1);

        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        var second = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        Assert.Null(second.RetryCount);
    }

    // --- cache ---

    [Fact]
    public async Task GeocodeAsync_HitsCache_OnSecondCallForSameAddress()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        var first = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        var second = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        Assert.Equal("address", first.Strategy);
        Assert.Equal("address", second.Strategy);
        // Nominatim must only be called once — second result came from cache
        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeocodeAsync_NormalizationCollapsesToSameCacheKey()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        // Three raw forms that all normalize to "100 Queen St W, Toronto, ON M5H 2N2"
        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        await _sut.GeocodeAsync("Apt 3 100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        await _sut.GeocodeAsync("Unit 7B 100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- concurrency ---

    [Fact]
    public async Task GeocodeAsync_ConcurrentRequestsForSameAddress_MakeOnlyOneNominatimCall()
    {
        var release = new TaskCompletionSource<NominatimSearchResult>();
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId))
            .ToArray();

        release.SetResult(new NominatimSearchResult(new[] { new NominatimResult("43.6532", "-79.3832", "Toronto") }, RetryCount: 1));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("address", r.Strategy));
        // Every waiter — not just the one that triggered the fetch — sees the same retry count.
        Assert.All(results, r => Assert.Equal(1, r.RetryCount));
        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Exactly 4 of the 5 concurrent callers should have logged dedup_join (the winner that
        // actually triggered the fetch does not log this event for itself).
        var dedupJoins = _logs.Lines.Count(l => l.Message.Contains("event=dedup_join"));
        Assert.Equal(4, dedupJoins);
    }

    [Fact]
    public async Task GeocodeAsync_ConcurrentDifferentRawFormsOfSameAddress_StillMakeOnlyOneNominatimCall()
    {
        // Unlike GeocodeAsync_NormalizationCollapsesToSameCacheKey (which awaits sequentially),
        // this fires three different raw inputs that all normalize identically at the same time,
        // racing the in-flight dedup guard against itself rather than against a settled cache.
        var release = new TaskCompletionSource<NominatimSearchResult>();
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);

        var tasks = new[]
        {
            _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId),
            _sut.GeocodeAsync("Apt 3 100 Queen St W, Toronto, ON M5H 2N2", RequestId),
            _sut.GeocodeAsync("Unit 7B 100 Queen St W, Toronto, ON M5H 2N2", RequestId),
        };

        release.SetResult(new NominatimSearchResult(new[] { new NominatimResult("43.6532", "-79.3832", "Toronto") }, RetryCount: 0));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("address", r.Strategy));
        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeocodeAsync_AfterInFlightFetchCompletes_SubsequentCallHitsCache_NotAnotherFetch()
    {
        // Guards against a leak in the in-flight ConcurrentDictionary: once a fetch finishes and
        // is removed from _inFlight, a later request for the same address must see the persisted
        // cache row — not dedup_join against a stale entry, and not trigger a second Nominatim call.
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        _logs.Lines.Clear();

        var second = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        Assert.Equal("address", second.Strategy);
        Assert.Contains(_logs.Lines, l => l.Message.Contains("event=cache_hit"));
        Assert.DoesNotContain(_logs.Lines, l => l.Message.Contains("event=dedup_join"));
        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeocodeAsync_TwoServiceInstances_ConcurrentFetchForSameAddress_HandlesCacheWriteRaceGracefully()
    {
        // The in-flight dedup map is per-instance (per-process, in the real deployment — see
        // README's "Multiple instances" scaling note). Two instances racing to persist the same
        // normalized address is the one scenario that still hits the DB's unique-index race that
        // PersistAsync's DbUpdateException catch (the "duplicate" event) exists to handle. A
        // single-instance test can't reach that path — GetOrAdd already serializes it away.
        var otherNominatim = Substitute.For<INominatimClient>();
        var release = new TaskCompletionSource<NominatimSearchResult>();
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);
        otherNominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);

        var otherInstance = new GeocodingService(
            new AddressNormalizer(),
            otherNominatim,
            new FixedConnectionFactory(_dbOptions),
            new CapturingLogger<GeocodingService>());

        var taskA = _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        var taskB = otherInstance.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        release.SetResult(new NominatimSearchResult(new[] { new NominatimResult("43.6532", "-79.3832", "Toronto") }, RetryCount: 0));
        var resultA = await taskA;
        var resultB = await taskB;

        // Both instances still return a correct, found result — one's DB insert silently loses
        // the unique-index race and is swallowed as benign rather than surfacing as an error.
        Assert.Equal("address", resultA.Strategy);
        Assert.Equal("address", resultB.Strategy);
        Assert.True(resultA.Found);
        Assert.True(resultB.Found);

        await using var db = new GeocodingDbContext(_dbOptions);
        var rowCount = db.CachedGeocodes.Count(c => c.NormalizedAddress == "100 Queen St W, Toronto, ON M5H 2N2");
        Assert.Equal(1, rowCount);
    }

    // --- logs ---

    [Fact]
    public async Task GeocodeAsync_LogsExpectedEventSequence_WithConsistentBatchRequestId_OnCacheMiss()
    {
        GivenNominatimReturns("45.4215", "-75.6972", "Ottawa");

        await _sut.GeocodeAsync("100 Wellington St, Ottawa, ON K1A 0A6", RequestId);

        var events = _logs.Lines.Select(l => ExtractField(l.Message, "event")).ToList();
        Assert.Equal(new[] { "start", "cache_miss", "nominatim_found", "persisted", "done" }, events);

        // Every line carries the same batch_request_id.
        Assert.All(_logs.Lines, l => Assert.Contains($"batch_request_id={RequestId}", l.Message));
    }

    [Fact]
    public async Task GeocodeAsync_LogsCacheHit_WithNoNominatimRequestId()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");
        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        _logs.Lines.Clear();

        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        var cacheHitLine = Assert.Single(_logs.Lines, l => l.Message.Contains("event=cache_hit"));
        Assert.Contains("nominatim_request_id=-", cacheHitLine.Message);
    }

    [Fact]
    public async Task GeocodeAsync_LogsPostalFallback_WithDistinctNominatimRequestIdFromAddressSearch()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(Array.Empty<NominatimResult>(), RetryCount: 0)));
        _nominatim.SearchByPostalCodeAsync("M5V3A8", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(
                new[] { new NominatimResult("43.6426", "-79.3871", "M5V, Toronto") }, RetryCount: 0)));

        await _sut.GeocodeAsync("99999 Nowhere Blvd, Toronto, ON M5V 3A8", RequestId);

        var fallbackLine = Assert.Single(_logs.Lines, l => l.Message.Contains("event=postal_fallback"));
        Assert.Contains("postal_code=\"M5V3A8\"", fallbackLine.Message);

        var foundLine = Assert.Single(_logs.Lines, l => l.Message.Contains("event=nominatim_found"));
        var fallbackNominatimId = ExtractField(fallbackLine.Message, "nominatim_request_id");
        var foundNominatimId = ExtractField(foundLine.Message, "nominatim_request_id");
        Assert.Equal(fallbackNominatimId, foundNominatimId);
        Assert.NotEqual("-", fallbackNominatimId);
    }

    [Fact]
    public async Task GeocodeAsync_LogsError_WithErrorMessageAndBatchRequestId()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Nominatim unavailable"));

        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        var errorLine = Assert.Single(_logs.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("event=error"));
        Assert.Contains($"batch_request_id={RequestId}", errorLine.Message);
        Assert.Contains("error=\"Nominatim unavailable\"", errorLine.Message);
    }

    // --- error isolation ---

    [Fact]
    public async Task GeocodeAsync_ReturnsErrorStrategy_WhenNominatimThrows()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Nominatim unavailable"));

        var result = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);

        Assert.Equal("error", result.Strategy);
        Assert.False(result.Found);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task GeocodeAsync_OneFailingAddress_DoesNotAffectOthers()
    {
        _nominatim.SearchByAddressAsync("100 Queen St W, Toronto, ON M5H 2N2", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("timeout"));
        _nominatim.SearchByAddressAsync("675 Belleville St, Victoria, BC V8W 9W2", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(
                new[] { new NominatimResult("48.4284", "-123.3656", "Victoria") }, RetryCount: 0)));

        var bad = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2", RequestId);
        var good = await _sut.GeocodeAsync("675 Belleville St, Victoria, BC V8W 9W2", RequestId);

        Assert.Equal("error", bad.Strategy);
        Assert.Equal("address", good.Strategy);
    }

    // --- helpers ---

    private void GivenNominatimReturns(string lat, string lon, string displayName, int retryCount = 0) =>
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NominatimSearchResult(new[] { new NominatimResult(lat, lon, displayName) }, retryCount)));

    // Pulls a logfmt "key=value" field out of a rendered log line — value may be bare
    // (nominatim_request_id=de0329b2) or quoted (raw_address="100 Main St"); either is returned
    // without the surrounding quotes.
    private static string ExtractField(string line, string key)
    {
        var marker = $"{key}=";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"Field '{key}' not found in: {line}");
        start += marker.Length;

        if (start < line.Length && line[start] == '"')
        {
            var end = line.IndexOf('"', start + 1);
            return line[(start + 1)..end];
        }

        var spaceIdx = line.IndexOf(' ', start);
        return spaceIdx < 0 ? line[start..] : line[start..spaceIdx];
    }

    private sealed class FixedConnectionFactory(DbContextOptions<GeocodingDbContext> opts)
        : IDbContextFactory<GeocodingDbContext>
    {
        public GeocodingDbContext CreateDbContext() => new(opts);
        public Task<GeocodingDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    // Captures every rendered log line (GeocodingService.Fmt produces the full message with no
    // template placeholders, so `formatter(state, exception)` already gives back the exact text)
    // instead of discarding it like NullLogger — needed to assert on log content/event sequencing.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
