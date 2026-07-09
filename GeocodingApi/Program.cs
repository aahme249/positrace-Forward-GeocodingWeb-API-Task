using GeocodingApi.Data;
using GeocodingApi.Models;
using GeocodingApi.Services;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers + JSON ────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── HTTP request/response logging ─────────────────────────────────────────────
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod
                    | HttpLoggingFields.RequestPath
                    | HttpLoggingFields.ResponseStatusCode
                    | HttpLoggingFields.Duration;
});

// ── Swagger / OpenAPI ─────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Positrace Geocoding API",
        Version = "v1",
        Description = "Forward geocoding of Canadian street addresses via Nominatim."
    });
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);
    c.OperationFilter<GeocodeExampleFilter>();
});

// ── Nominatim HttpClient ──────────────────────────────────────────────────────
var nominatimBase  = builder.Configuration["Nominatim:BaseUrl"] ?? "https://nominatim.openstreetmap.org/";
var userAgent      = builder.Configuration["Nominatim:UserAgent"]
                     ?? "Positrace-Geocoding-Service/1.0 (asifahmed3959@gmail.com)";
var timeoutSeconds        = builder.Configuration.GetValue<int>("Nominatim:TimeoutSeconds", 5);
var retryCount            = builder.Configuration.GetValue<int>("Nominatim:RetryCount", 3);
var retryDelay            = builder.Configuration.GetValue<int>("Nominatim:RetryDelaySeconds", 2);
var cbFailures            = builder.Configuration.GetValue<int>("Nominatim:CircuitBreakerFailures", 5);
var cbBreakSeconds        = builder.Configuration.GetValue<int>("Nominatim:CircuitBreakerBreakSeconds", 30);

builder.Services.AddHttpClient("nominatim", client =>
{
    client.BaseAddress = new Uri(nominatimBase);
    client.DefaultRequestHeaders.Add("User-Agent", userAgent);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds * (retryCount + 1)); // outer timeout covers all retries
})
.AddResilienceHandler("nominatim-resilience", pipeline =>
{
    // Pipeline order (outermost → innermost):
    //   Retry → CircuitBreaker → Timeout
    // Retry wraps everything; circuit breaker tracks failures across attempts;
    // timeout applies per individual attempt.

    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = retryCount,
        Delay            = TimeSpan.FromSeconds(retryDelay),
        BackoffType      = DelayBackoffType.Constant,
        ShouldHandle     = args => args.Outcome switch
        {
            { Exception: HttpRequestException or TaskCanceledException } => PredicateResult.True(),
            { Result.IsSuccessStatusCode: false }                        => PredicateResult.True(),
            _                                                            => PredicateResult.False()
        }
    });

    // Circuit breaker: if ≥cbFailures of the last calls in a 30s window fail,
    // stop calling Nominatim for cbBreakSeconds, then allow one probe call.
    // HttpCircuitBreakerStrategyOptions already handles 5xx/timeouts/HttpRequestException.
    pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        FailureRatio       = 1.0,                                    // open when 100% of sampled calls fail
        MinimumThroughput  = cbFailures,                             // need at least this many calls to trigger
        SamplingDuration   = TimeSpan.FromSeconds(30),               // measure failures over 30s window
        BreakDuration      = TimeSpan.FromSeconds(cbBreakSeconds),   // stay open this long, then half-open probe
    });

    pipeline.AddTimeout(TimeSpan.FromSeconds(timeoutSeconds));
});

// ── Persistent cache (SQLite + EF Core) ──────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=geocoding.db";
builder.Services.AddDbContextFactory<GeocodingDbContext>(options =>
    options.UseSqlite(connStr));

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<IAddressNormalizer, AddressNormalizer>();
// NominatimClient is singleton: it owns the TokenBucketRateLimiter that enforces 1 req/sec to Nominatim
builder.Services.AddSingleton<INominatimClient, NominatimClient>();
// GeocodingService is singleton: it owns the in-flight ConcurrentDictionary used for deduplication
builder.Services.AddSingleton<IGeocodingService, GeocodingService>();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Ensure the SQLite schema exists on startup (idempotent — no EF migrations needed)
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GeocodingDbContext>>();
    await using var ctx = await dbFactory.CreateDbContextAsync();
    await ctx.Database.EnsureCreatedAsync();
}

app.UseHttpLogging();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Positrace Geocoding API v1"));

app.MapControllers();
app.Run();
