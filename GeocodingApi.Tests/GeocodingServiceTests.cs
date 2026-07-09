using GeocodingApi.Data;
using GeocodingApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace GeocodingApi.Tests;

public sealed class GeocodingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly INominatimClient _nominatim;
    private readonly GeocodingService _sut;

    public GeocodingServiceTests()
    {
        // Shared in-memory SQLite — connection must stay open for the schema to persist across factory calls
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<GeocodingDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var seed = new GeocodingDbContext(options);
        seed.Database.EnsureCreated();

        _nominatim = Substitute.For<INominatimClient>();
        _sut = new GeocodingService(
            new AddressNormalizer(),
            _nominatim,
            new FixedConnectionFactory(options),
            NullLogger<GeocodingService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    // --- happy path ---

    [Fact]
    public async Task GeocodeAsync_ReturnsAddressStrategy_OnNominatimHit()
    {
        GivenNominatimReturns("45.4215", "-75.6972", "Ottawa");

        var result = await _sut.GeocodeAsync("100 Wellington St, Ottawa, ON K1A 0A6");

        Assert.Equal("address", result.Strategy);
        Assert.True(result.Found);
        Assert.Equal(45.4215, result.Latitude);
    }

    [Fact]
    public async Task GeocodeAsync_NormalizesAddressBeforeLookup()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        await _sut.GeocodeAsync("Unit 5 100 King St W, Toronto, ON M5X 1A9");

        // Nominatim must receive the stripped address, not the raw input
        await _nominatim.Received(1)
            .SearchByAddressAsync("100 King St W, Toronto, ON M5X 1A9", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeocodeAsync_FallsBackToPostalCode_WhenAddressNotFound()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NominatimResult[]?>(Array.Empty<NominatimResult>()));
        _nominatim.SearchByPostalCodeAsync("M5V3A8", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NominatimResult[]?>(
                new[] { new NominatimResult("43.6426", "-79.3871", "M5V, Toronto") }));

        var result = await _sut.GeocodeAsync("99999 Nowhere Blvd, Toronto, ON M5V 3A8");

        Assert.Equal("postal_code", result.Strategy);
        Assert.True(result.Found);
    }

    [Fact]
    public async Task GeocodeAsync_ReturnsNotFound_WhenBothSearchesFail()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NominatimResult[]?>(Array.Empty<NominatimResult>()));

        var result = await _sut.GeocodeAsync("Unit 5 99999 Nowhere Blvd, Faketown, ON");

        Assert.Equal("not_found", result.Strategy);
        Assert.False(result.Found);
        Assert.Null(result.Latitude);
    }

    // --- cache ---

    [Fact]
    public async Task GeocodeAsync_HitsCache_OnSecondCallForSameAddress()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        var first = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2");
        var second = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2");

        Assert.Equal("address", first.Strategy);
        Assert.Equal("address", second.Strategy);
        // Nominatim must only be called once — second result came from cache
        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GeocodeAsync_NormalizationCollapsesToSameCacheKey()
    {
        GivenNominatimReturns("43.6532", "-79.3832", "Toronto");

        // Three raw forms that all normalize to "100 Queen St W, Toronto, ON M5H 2N2"
        await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2");
        await _sut.GeocodeAsync("Apt 3 100 Queen St W, Toronto, ON M5H 2N2");
        await _sut.GeocodeAsync("Unit 7B 100 Queen St W, Toronto, ON M5H 2N2");

        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- concurrency ---

    [Fact]
    public async Task GeocodeAsync_ConcurrentRequestsForSameAddress_MakeOnlyOneNominatimCall()
    {
        var release = new TaskCompletionSource<NominatimResult[]?>();
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2"))
            .ToArray();

        release.SetResult(new[] { new NominatimResult("43.6532", "-79.3832", "Toronto") });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("address", r.Strategy));
        await _nominatim.Received(1).SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- error isolation ---

    [Fact]
    public async Task GeocodeAsync_ReturnsErrorStrategy_WhenNominatimThrows()
    {
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Nominatim unavailable"));

        var result = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2");

        Assert.Equal("error", result.Strategy);
        Assert.False(result.Found);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task GeocodeAsync_OneFailingAddress_DoesNotAffectOthers()
    {
        _nominatim.SearchByAddressAsync("100 Queen St W, Toronto, ON M5H 2N2", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("timeout"));
        _nominatim.SearchByAddressAsync("675 Belleville St, Victoria, BC V8W 9W2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NominatimResult[]?>(
                new[] { new NominatimResult("48.4284", "-123.3656", "Victoria") }));

        var bad = await _sut.GeocodeAsync("100 Queen St W, Toronto, ON M5H 2N2");
        var good = await _sut.GeocodeAsync("675 Belleville St, Victoria, BC V8W 9W2");

        Assert.Equal("error", bad.Strategy);
        Assert.Equal("address", good.Strategy);
    }

    // --- helpers ---

    private void GivenNominatimReturns(string lat, string lon, string displayName) =>
        _nominatim.SearchByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NominatimResult[]?>(new[] { new NominatimResult(lat, lon, displayName) }));

    private sealed class FixedConnectionFactory(DbContextOptions<GeocodingDbContext> opts)
        : IDbContextFactory<GeocodingDbContext>
    {
        public GeocodingDbContext CreateDbContext() => new(opts);
        public Task<GeocodingDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
