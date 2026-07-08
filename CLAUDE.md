# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Run locally (from GeocodingApi/) — Swagger at http://localhost:5050/swagger
cd GeocodingApi && dotnet run

# Build / restore
dotnet build GeocodingApi/GeocodingApi.csproj

# Run via Docker — Swagger at http://localhost:8080/swagger, cache persisted in the `geocoding-data` volume
docker compose up --build
```

There is no automated test project (see "Testing" below). To exercise the API manually, use `requests.http`
(VS Code REST Client / JetBrains HTTP Client) or POST the fixtures in `samples/*.json` to
`POST /api/v1/geocode`.

## Architecture

Single-project ASP.NET Core 9 Web API (`GeocodingApi/`) with one controller action
(`POST /api/v1/geocode`) that forward-geocodes a batch of Canadian addresses against the public
Nominatim API. The request flow through the layers:

```
Controller → GeocodingService.GeocodeAsync (per address, run concurrently via Task.WhenAll)
               ├─ AddressNormalizer.Normalize        (strip unit/apt qualifiers)
               ├─ SQLite cache lookup (by normalized address)   — fast path, no locking
               ├─ in-flight ConcurrentDictionary                — dedupe concurrent identical requests
               └─ NominatimClient (rate-limited HTTP)
                     ├─ SearchByAddressAsync           (primary)
                     └─ SearchByPostalCodeAsync         (fallback if primary yields no results)
```

Key invariants, each load-bearing for one of the assessment requirements — don't break these
when refactoring:

- **Cache key is the normalized address, not the raw input.** `CachedGeocode.NormalizedAddress`
  has a unique index (`GeocodingDbContext`). This is a deliberate choice (collapses duplicate raw
  inputs that normalize the same way) — see the README's "Persistent cache" section before
  changing it.
- **`GeocodingService` and `NominatimClient` are registered as singletons** (`Program.cs`), not
  scoped/transient — `NominatimClient` owns the rate-limiting `SemaphoreSlim`, and
  `GeocodingService` owns the in-flight dedup `ConcurrentDictionary`. Either state has to live for
  the app's lifetime to work; the DbContext itself is still obtained per-operation via
  `IDbContextFactory<GeocodingDbContext>` since `DbContext` is not thread-safe.
- **Rate limiting lives inside `NominatimClient`, below both the address and postal-code query
  paths** — every outbound call (not just the primary one) goes through the same
  `SemaphoreSlim(1,1)` + `_lastCallAt` throttle, so a request that hits the fallback path still
  respects the ≥1s Nominatim policy.
- **In-flight dedup and the persistent cache are separate mechanisms with different scopes.** The
  cache check happens first and is unconditional; the in-flight map only comes into play on a
  cache miss, and only for the duration of one outbound fetch (see `GeocodingService.GetOrFetchAsync`).
- **Per-address error isolation:** `GeocodingService.GeocodeAsync` catches exceptions per address,
  not per batch, and returns `strategy: "error"` so one bad address doesn't fail the whole
  response. Since this catch sits inside the in-flight task, exceptions propagate to every
  concurrent waiter on that same address via `TaskCompletionSource.SetException`.
- **`AddressNormalizer`'s regex passes run in a fixed order** (dash-unit strip first, then
  named-qualifier strips, then whitespace/comma cleanup) — the dash-unit pass must run before the
  others because it anchors on the start of the string, which later passes can alter.

## Configuration

All tunables are in `appsettings.json` (`ConnectionStrings:DefaultConnection`, `Nominatim:BaseUrl`,
`Nominatim:UserAgent`) or overridden via environment variables in `docker-compose.yml`
(`Nominatim__UserAgent`, etc.). The Nominatim `User-Agent` must stay a real, resolvable contact
per Nominatim's usage policy — don't replace it with a placeholder.

## Testing

No xUnit/test project exists yet. If adding one, the natural seams are `AddressNormalizer` (pure,
easily unit-tested) and `GeocodingService`/`NominatimClient` (need `INominatimClient` /
`IAddressNormalizer` fakes — both are already interfaces for this reason). `samples/*.json` map
1:1 to normalization rules and should be mirrored as test cases if a test project is added.
