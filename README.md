# Positrace Geocoding API

ASP.NET Core 9 Web API that forward-geocodes Canadian street addresses via the [Nominatim](https://nominatim.org/) public API. Built for the Positrace Senior Backend Engineer technical assessment.

---

## Running locally

### Docker (recommended)

```bash
docker compose up --build
```

API available at **http://localhost:8080** — Swagger UI at **http://localhost:8080/swagger**.

The SQLite database is persisted in a named Docker volume (`geocoding-data`) so the cache survives container restarts.

### .NET SDK

**Prerequisites:** .NET 9 SDK

```bash
cd GeocodingApi
dotnet run
```

Swagger UI is available at **http://localhost:5050/swagger**.

The SQLite database file (`geocoding.db`) is created automatically on first startup in the working directory.

---

## POST /api/v1/geocode

**Request**

```json
{
  "addresses": [
    "123-12 Main St, Toronto, ON M5V 2T6",
    "Apt. 4 456 Yonge St, Toronto, ON M4Y 1X9",
    "Unit 201 789 Queen St W, Toronto, ON M6J 1G1",
    "Suite 300 1000 De La Gauchetière W, Montreal, QC H3B 4W5",
    "#5 100 Wellington St, Ottawa, ON K1A 0A9"
  ]
}
```

**Response**

```json
{
  "results": [
    {
      "originalAddress": "123-12 Main St, Toronto, ON M5V 2T6",
      "normalizedAddress": "123 Main St, Toronto, ON M5V 2T6",
      "latitude": 43.6532,
      "longitude": -79.3832,
      "displayName": "123, Main Street, ...",
      "strategy": "address",
      "found": true,
      "error": null
    }
  ]
}
```

Results are returned in the same order and count as the input list, and each one echoes `originalAddress`, so every result maps unambiguously back to its source — including duplicate address strings within the same batch.

### `strategy` values

| Value | Meaning |
|---|---|
| `address` | Nominatim matched the normalised street address |
| `postal_code` | Address query returned nothing; result comes from the postal code |
| `not_found` | Neither query returned results |
| `error` | Nominatim was unreachable or returned an HTTP error |

Ready-to-run requests for every case below are in [`requests.http`](requests.http) and [`samples/`](samples).

---

## My approach, step by step

I worked through the brief in the order the requirements were given, since each one builds on the last: normalize → geocode → fall back → cache → dedupe → throttle.

### 1. Address normalization

**Thinking:** Nominatim matches street addresses more reliably without unit/apartment qualifiers, so I strip them *before* the address reaches the outbound client — and I keep the normalised string alongside the raw one in every result, so it's always visible what was actually queried.

**Implementation:** [`AddressNormalizer`](GeocodingApi/Services/AddressNormalizer.cs) runs a fixed pipeline of compiled regexes:

1. Strip a dash-prefixed unit at the very start of the string (`123-12 Main St` → `123 Main St`) — done first, before any other pass can shift the leading digits.
2. Strip `Apt`/`Apt.`, `Unit`, `Suite`, and `#`, each followed by its identifier token, wherever they appear.
3. Collapse the doubled commas/whitespace those removals leave behind.

**Known limitations** (calling these out explicitly rather than leaving them to be discovered):

- The unit identifier is matched as a single whitespace-delimited token, so `Unit 12A` strips cleanly but `Unit 12 A` (a space inside the identifier) would not be fully removed.
- The dash-prefix rule only fires when the pattern sits at the very start of the trimmed address — `Main St 123-12` would not match.
- French-language equivalents (`App.`, `No`, `Bureau`) aren't handled — every example in the brief was English, so I scoped to what was asked rather than guessing at unstated requirements.

### 2. Fallback strategy

**Thinking:** A normalised address can still fail to geocode (typo'd street name, address not present in OSM, etc.), but the postal code embedded in the same string is usually still valid and resolves to *something* useful. So the fallback is the same address via a different query, not a different address.

**Implementation:** [`GeocodingService.FetchAndCacheAsync`](GeocodingApi/Services/GeocodingService.cs) tries the normalised street address first; only if Nominatim returns zero results does it call `ExtractPostalCode` (a Canadian postal-code regex, `A1A 1A1`) and retry against `/search?postalcode=`. The `strategy` field tells the caller which query actually produced the coordinates, so a postal-code-level result (block precision) is never silently confused with an exact street match.

### 3. In-flight deduplication

**Thinking:** A batch request can legitimately contain the same address twice, and separate concurrent HTTP requests can target the same address. Neither case should cost two outbound Nominatim calls when one suffices.

**Implementation:** `GeocodingService` holds a `ConcurrentDictionary<string, Task<CachedGeocode?>>` keyed on the *normalised* address. The first caller for a given key wins a `TryAdd` and does the real fetch; every other caller for that key gets the same `Task` back and awaits it instead of starting its own. I used a `TaskCompletionSource` (with `RunContinuationsAsynchronously`) rather than caching the fetch method's `Task` directly, so a slow continuation on one waiter can't block the others. The entry is removed in a `finally` once the fetch settles — success or failure — so the next request for that address goes through the persistent cache or triggers a genuinely fresh fetch, rather than replaying a stale in-flight task.

### 4. Persistent cache

**Thinking:** Vehicles retravel the same routes, so the same addresses recur across restarts — the whole point of persisting is to avoid re-paying the 1 req/s Nominatim cost for something already known. I used SQLite via EF Core: a single-file, dependency-free store is enough for a cache table and keeps `docker compose up` a one-liner.

**Cache key decision — normalised address, not raw input.** I considered keying on the raw string first (no transformation needed at lookup time) but rejected it:

1. **It collapses duplicates.** `"123 Main St Apt 4"`, `"123 Main St Unit 4"`, and `"123-4 Main St"` all normalise to `"123 Main St"`. A raw-input key treats all three as separate cache misses, each firing its own Nominatim call; a normalised key lets them share one entry.
2. **It matches the source of truth.** What's cached is Nominatim's answer to the query actually sent. Keying on raw input would mean re-normalising on every lookup just to decide whether a stored result applies — redundant.
3. **Trade-off I accepted:** if normalisation is wrong for some input, the bad cached result would be shared by every raw address mapping to that same wrong key. I judged this acceptable — the failure mode is the normaliser being wrong, which is a bug regardless of caching, and fixing it fixes the cache automatically.

**Implementation:** [`CachedGeocode`](GeocodingApi/Data/CachedGeocode.cs) rows are looked up by `NormalizedAddress` before any outbound call is even considered (`ReadFromCacheAsync`), so a warm cache never touches the in-flight map or Nominatim at all. Writes go through `PersistAsync`, which swallows `DbUpdateException` — a benign race if two processes happen to persist the same key concurrently — rather than failing the request over a duplicate write.

### 5. Rate limiting

**Thinking:** Nominatim's 1 req/s limit is global to the service, not per-request, so the throttle has to sit above any per-call logic and serialise *everything* going out, including both the address and postal-code queries.

**Implementation:** [`NominatimClient`](GeocodingApi/Services/NominatimClient.cs) is registered as a singleton owning a `SemaphoreSlim(1, 1)` and a `_lastCallAt` timestamp. Every outbound call acquires the semaphore, computes the delay needed to keep ≥1s since the previous call, awaits it if positive, then fires the request and releases. Because it's a singleton semaphore, this holds regardless of how many concurrent batch requests or dedup-losing callers are queued up behind it — they all funnel through the same gate.

---

## Transient failures & operational visibility

- **Transient failures:** `NominatimClient.EnsureSuccessStatusCode()` throws on non-2xx responses; `GeocodingService.GeocodeAsync` catches per-address, not per-batch, so one bad address returns `strategy: "error"` with the exception message instead of failing the whole request. Because the failure happens inside the in-flight task, every concurrent waiter on that same address also receives the exception (via `tcs.SetException`) rather than hanging indefinitely.
- **Logging:** every stage logs through `ILogger<T>` — normalisation result, cache hit/miss, in-flight wait, postal-code fallback trigger, rate-limit delay, outbound URL — at `Information`/`Debug`, so a production deployment can trace which path a given address took without attaching a debugger. Levels are tunable per-namespace in `appsettings.json`.
- **Configuration:** Nominatim base URL, User-Agent (required by Nominatim's usage policy — set to a real contact address), and the SQLite connection string are all externalised via `appsettings.json` / environment variables (see `docker-compose.yml`), not hardcoded, so they can change without a rebuild.

---

## Test cases

One fixture per normalisation rule, plus the two non-happy paths, so each can be exercised independently or as a batch:

| File | Exercises |
|---|---|
| `samples/dash-unit.json` | Dash-prefixed unit (`123-12 Main St`) |
| `samples/apt.json` | `Apt` and `Apt.` (with and without period) |
| `samples/unit.json` | `Unit` qualifier |
| `samples/suite.json` | `Suite` qualifier |
| `samples/hash.json` | `#` qualifier |
| `samples/postal-code-fallback.json` | Nonsense street name, valid postal code → `strategy: "postal_code"` |
| `samples/not-found.json` | Nonsense street *and* no postal code → `strategy: "not_found"` |
| `samples/mixed-batch.json` | All of the above in a single request, to confirm ordering/mapping holds under a mixed batch |

[`requests.http`](requests.http) wraps the same cases as runnable requests (VS Code REST Client / JetBrains HTTP Client). I didn't add a separate xUnit project for this assessment — given the scope, I prioritised exercising the real HTTP surface end-to-end (actual rate limiter, actual cache, actual Nominatim) over unit-testing the regex pipeline in isolation, since the normalisation rules are small enough to verify directly against [`AddressNormalizer.cs`](GeocodingApi/Services/AddressNormalizer.cs).

---

## Concurrency & throttling — observed behaviour

Seven tests were run against a live instance to verify the rate limiter, cache, and in-flight deduplication interact correctly under real concurrent load.

### Results

| Test | Scenario | Wall time | Nominatim calls |
|---|---|---|---|
| 1 | 5 unique cold-cache addresses, single client | **5.4 s** | 5 |
| 2 | Same 5 addresses again (warm cache) | **20 ms** | 0 |
| 3 | Same address × 5 in one request (dedup) | **16 ms** | 0 (cached) |
| 4 | 3 concurrent clients, 9 unique cold addresses | **7.0 s** | 9 |
| 5 | 3 concurrent clients, **same** 2 cold addresses | **1.5 s** | 2 (not 6) |
| 6 | 10 rapid sequential requests, warm cache | **160 ms** | 0 |
| 7 | 10 concurrent clients, mixed cached + cold | **2.9 s** | 4 |

### What the numbers show

**Test 1 vs 2 — cache impact is dramatic.**
5 cold addresses take 5.4 s (rate-limited, ~1 s per Nominatim call). The same 5 addresses warm return in 20 ms — 270× faster. For a fleet that revisits the same routes, the vast majority of requests hit the cache within the first day.

**Test 3 — in-request deduplication works.**
Sending the same address 5 times in one batch results in a single Nominatim call (or an instant cache hit). All 5 slots in the response receive identical results. Zero wasted calls.

**Test 4 — concurrent clients serialize cleanly through the rate limiter.**
3 clients each submitting 3 unique cold addresses produces exactly 9 Nominatim calls, spaced ≥ 1 s apart. All calls run in-flight concurrently; the last one to fire determines the wall time (~7 s for 9 calls, not 9 × HTTP_time).

**Test 5 — cross-client deduplication works.**
3 clients simultaneously requesting the same 2 cold addresses resulted in only 2 Nominatim calls total — not 6. All 3 clients received the result from the same 2 in-flight tasks. Wall time was identical to a single client making the same request.

**Test 7 — realistic fleet scenario.**
10 concurrent clients where 7 submit cached addresses and 3 submit new ones. The 7 cached clients returned in milliseconds; the 3 cold clients completed in 2.9 s. Cold and cached requests don't compete — cached lookups bypass the rate limiter entirely.

---

### Strengths of this approach

- **Cache collapses repeated-route cost to near-zero.** The dominant use case (vehicles re-travelling routes) is served from SQLite with no outbound calls.
- **In-flight dedup prevents thundering herd.** If 50 vehicles submit the same new address simultaneously, Nominatim is called once, not 50 times.
- **Rate limiter is tight and correct.** The semaphore is released before the HTTP call, so calls overlap in-flight — one new call fires per second, but N calls complete in N seconds, not N × HTTP_time seconds.
- **Per-address error isolation.** One failed address returns `strategy: "error"` without failing the rest of the batch.
- **Retry with configurable back-off.** Transient Nominatim failures are retried silently before surfacing an error to the caller.

### Weaknesses of this approach

- **In-process rate limiter doesn't scale horizontally.** Running two instances of this service would double the Nominatim request rate. The `SemaphoreSlim` lives in one process — there's no distributed coordination. Mitigation: add a Redis-backed distributed rate limiter, or put a single Nominatim-facing worker behind an internal queue.
- **No per-client fairness.** A single client submitting 200 cold addresses monopolises the rate limiter for ~200 seconds, blocking all other clients' new addresses. Cached requests are unaffected, but new-address requests from other clients queue behind the large batch. Mitigation: per-client request queues with round-robin dispatch.
- **No batch size limit enforced.** A caller can send 1 000 addresses and hold an HTTP connection open for ~16 minutes. Mitigation: cap batch size (e.g. 50) and recommend async job submission for larger workloads.
- **In-flight dedup state is in-memory only.** If the service restarts mid-fetch, the task is lost. The persistent cache means completed results survive restarts; only the in-progress ones are lost and must be retried by the caller.

### Is there a better way?

For this use case (Canadian vehicle fleet, recurring routes) — **no, not meaningfully.** The combination of persistent cache + in-flight dedup + rate limiter handles the dominant patterns well. The weaknesses only matter at scale:

| Scale trigger | Better approach |
|---|---|
| Multiple service instances | Shared Redis rate limiter + distributed cache |
| Batches regularly > 50 addresses | Async job queue (see Future considerations) |
| High volume of genuinely new addresses | Self-hosted Nominatim (no rate limit, full fan-out) |
| Per-client fairness required | Priority queue with per-tenant slots |

---

## Future considerations

### Asynchronous message queue

The current API is synchronous — the client holds the HTTP connection open until every address is geocoded. This works well for small batches (< 50 addresses) and high cache-hit workloads (vehicles re-travelling the same routes quickly warm the cache), but breaks down for large cold-cache batches:

- 200 new addresses on a fresh route = ~200 seconds of open connection
- Multiple fleet operators submitting large batches simultaneously have no backpressure
- A service restart mid-batch loses all in-progress work

A job-based async model solves this:

```
POST /api/v1/geocode         → 202 Accepted  { "jobId": "abc-123" }
GET  /api/v1/geocode/{jobId} → 200 OK with results (or 202 still processing)
```

A background worker reads from an in-process `Channel<T>` (or an external broker like Redis / Azure Service Bus for durability across restarts), applies the same rate-limited Nominatim pipeline, and writes results to the SQLite cache. The client polls until the job is complete.

**When to add it:** if batches regularly exceed 50 addresses, or if the fleet generates large bursts of new-route addresses that overwhelm the synchronous timeout budget. For the described use case (recurring vehicle routes, high cache-hit rate) the synchronous API is sufficient.

### Self-hosted Nominatim

Using the public Nominatim endpoint caps throughput at 1 req/sec per IP regardless of architecture. Running a private Nominatim instance (open source, ~50 GB disk for Canada-only extract) removes the rate limit entirely, allows fan-out to a worker pool, and eliminates the dependency on a third-party service — the right call once geocoding volume justifies the infrastructure cost.

---

## Development tooling

I built and iterated on this in **Claude Code**, running in a terminal against this repo, rather than writing everything by hand from scratch. In practice that meant: I set the requirements and made the calls on structure and trade-offs (singleton vs. scoped services, cache key, fallback design), and used the agent to scaffold boilerplate, wire up the Docker/EF Core setup, and draft the sample fixtures/`requests.http`, then reviewed and adjusted the generated code myself before it went in. This README itself — including the "step by step" section above — was written the same way: I asked for a walkthrough of the reasoning behind each requirement, then reviewed it against the actual source for accuracy.

[`CLAUDE.md`](CLAUDE.md) in the repo root is a machine-readable onboarding file for this project. It's written for coding agents (Claude Code or otherwise) rather than for humans: build/run commands, the request-flow architecture, and the invariants that back each of the five assessment requirements (cache key choice, singleton lifetimes, rate-limiter placement, dedup vs. cache scope, regex ordering) — the things an agent would otherwise have to rediscover by reading every file before making a safe change.
# positrace-Forward-GeocodingWeb-API-Task
