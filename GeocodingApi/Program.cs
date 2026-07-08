using GeocodingApi.Data;
using GeocodingApi.Models;
using GeocodingApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers + JSON ────────────────────────────────────────────────────────
builder.Services.AddControllers();

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
var timeoutSeconds = builder.Configuration.GetValue<int>("Nominatim:TimeoutSeconds", 5);
var retryCount     = builder.Configuration.GetValue<int>("Nominatim:RetryCount", 3);
var retryDelay     = builder.Configuration.GetValue<int>("Nominatim:RetryDelaySeconds", 2);

builder.Services.AddHttpClient("nominatim", client =>
{
    client.BaseAddress = new Uri(nominatimBase);
    client.DefaultRequestHeaders.Add("User-Agent", userAgent);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds * (retryCount + 1)); // outer timeout covers all retries
})
.AddResilienceHandler("nominatim-retry", pipeline =>
{
    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = retryCount,
        Delay             = TimeSpan.FromSeconds(retryDelay),
        BackoffType       = DelayBackoffType.Constant,
        // Retry on transient HTTP errors and timeouts only — not on 4xx
        ShouldHandle      = args => args.Outcome switch
        {
            { Exception: HttpRequestException or TaskCanceledException } => PredicateResult.True(),
            { Result.IsSuccessStatusCode: false }                        => PredicateResult.True(),
            _                                                            => PredicateResult.False()
        }
    });
    pipeline.AddTimeout(TimeSpan.FromSeconds(timeoutSeconds));
});

// ── Persistent cache (SQLite + EF Core) ──────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=geocoding.db";
builder.Services.AddDbContextFactory<GeocodingDbContext>(options =>
    options.UseSqlite(connStr));

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<IAddressNormalizer, AddressNormalizer>();
// NominatimClient is singleton: it owns the rate-limiter SemaphoreSlim that serialises all outbound calls
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

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Positrace Geocoding API v1"));

app.MapControllers();
app.Run();
