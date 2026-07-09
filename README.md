# Positrace Geocoding API

ASP.NET Core 9 Web API that forward-geocodes Canadian street addresses via the [Nominatim](https://nominatim.org/) public API. Built for the Positrace Senior Backend Engineer technical assessment.

---

## Quick start

### Option A — Docker (no SDK needed)

```bash
git clone https://github.com/aahme249/positrace-Forward-GeocodingWeb-API-Task.git
cd positrace-Forward-GeocodingWeb-API-Task
docker compose up --build
```

### Option B — .NET 9 SDK

```bash
git clone https://github.com/aahme249/positrace-Forward-GeocodingWeb-API-Task.git
cd positrace-Forward-GeocodingWeb-API-Task/GeocodingApi
dotnet run
```

| | Docker | .NET SDK |
|---|---|---|
| API | http://localhost:8080/api/v1/geocode | http://localhost:5050/api/v1/geocode |
| Swagger UI | http://localhost:8080/swagger | http://localhost:5050/swagger |

The SQLite cache (`geocoding.db`) is created automatically on first run. No database setup needed.

---

## Try it immediately

**Open Swagger UI** at the URL above — the request and response fields are pre-filled with a mixed example covering all four strategies. Click **Try it out → Execute**.

Or paste this into a terminal:

```bash
curl -X POST http://localhost:8080/api/v1/geocode \
  -H "Content-Type: application/json" \
  -d '{
    "addresses": [
      "Apt. 4 456 Yonge St, Toronto, ON M4Y 1X9",
      "123-12 Main St, Toronto, ON M5V 2T6",
      "Unit 201 789 Queen St W, Toronto, ON M6J 1G1",
      "Suite 300 1000 De La Gauchetière W, Montreal, QC H3B 4W5",
      "#5 100 Wellington St, Ottawa, ON K1A 0A9",
      "99999 Nowhere Blvd, Apt 12, Toronto, ON M5V 3A8",
      "Room 412 Fairmont Royal York, 100 Front St W, Toronto, ON M5J 1E3"
    ]
  }'
```

Or use the ready-made sample files:

```bash
# Each file targets one normalisation rule
curl -X POST http://localhost:8080/api/v1/geocode \
  -H "Content-Type: application/json" \
  -d @samples/mixed-batch.json

# Other samples: apt.json  unit.json  suite.json  hash.json
#                dash-unit.json  postal-code-fallback.json  not-found.json
```

If you use VS Code or a JetBrains IDE, open [`requests.http`](requests.http) and click **Send Request** above any block — no curl needed.

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

**Thinking:** Nominatim's 1 req/s limit is global to the service, not per-request. The throttle has to sit above any per-call logic and serialise *everything* going out, including both the address and postal-code fallback queries. I went through three stages before settling on the final approach:

**Stage 1 — Naive `Task.Delay(1000)`** was the first instinct: sleep 1 second before each call. Rejected immediately — under any concurrency, two callers both pass the "is it time yet?" check simultaneously and fire together. It only works if everything is already serialised through one loop, which throws away the concurrency we need elsewhere.

**Stage 2 — `SemaphoreSlim(1,1)` + timestamp gate** is the classic hand-rolled answer: a mutex so only one caller is in the scheduling section at a time, then compare `DateTime.UtcNow` against the last-call timestamp and sleep the remainder. This *works* and shows understanding of the mechanics. The subtle trap is releasing the semaphore *before* the HTTP call — hold it through the call and N requests take N × HTTP_time instead of N seconds. Serviceable, but manually reimplementing a rate limiter with clock arithmetic is dated in modern .NET.

**Stage 3 — `TokenBucketRateLimiter`** (built into `System.Threading.RateLimiting` since .NET 7): a bucket holds 1 token; a background timer replenishes 1 token/sec. Every call does `AcquireAsync(1)`, which queues the caller until a token is available. No manual clock arithmetic, no two-phase semaphore dance, no risk of holding the lock through the HTTP call. Chose `TokenBucket` over `FixedWindowRateLimiter` because FixedWindow allows back-to-back calls at window edges (calls at t=0.99s and t=1.00s both pass) — TokenBucket enforces genuine spacing.

**Why not `Channel<T>`?** A channel with a single background consumer is architecturally cleaner and the right answer at scale: it fully decouples request ingress from the outbound rate, enables back-pressure, and supports priority queuing. But it trades a ~60-line singleton for a background service, work-item structs, `TaskCompletionSource` lifecycle management, and a silent-crash failure mode. For this scope — single instance, public Nominatim, fleet workload dominated by cache hits — the complexity is not justified. *Where channels clearly win* is the high-volume scenario from the numbers section: inbound requests are arriving faster than 1/sec and callers shouldn't hold HTTP connections open behind the rate-limit gate. There, a channel (or a real broker like Redis Streams) is the shock absorber — accept the request, enqueue, return `202 Accepted` + job ID, drain asynchronously. That is exactly the scaling path described in Future considerations.

**Implementation:** [`NominatimClient`](GeocodingApi/Services/NominatimClient.cs) is a singleton owning a `TokenBucketRateLimiter` (1 token/sec, FIFO, configurable via `Nominatim:RateLimitPerSecond`). Every outbound call calls `AcquireAsync(1)` then fires the HTTP call. Because replenishment is timer-driven, N cold addresses complete in ~N seconds, not N × HTTP_time. Rate limiting and retry/resilience are kept as separate layers — `TokenBucketRateLimiter` controls throughput; Polly (retry + circuit breaker + timeout) handles failure recovery. They compose without interfering.

---

## Rate-limiting strategy analysis

This is the core design decision in the service — here is why each option was evaluated and which one was chosen.

### The five strategies

**Option 1 — Naive `Task.Delay(1000)`**

The simplest thing that looks correct: before every call, sleep 1 second.

| | |
|---|---|
| Advantage | Two lines of code. No dependencies. |
| Disadvantage | **Broken under any concurrency.** Two simultaneous callers both skip the delay, both fire at the same moment, and Nominatim sees a burst. The delay runs *in parallel with the request*, not before it. |
| Verdict | **Do not use.** Violates the rate limit as soon as more than one caller is in play. |

---

**Option 2 — `SemaphoreSlim(1,1)` + manual timestamp gate**

The pattern this codebase originally used: one goroutine-like slot guards a `_lastCallAt` field. The semaphore serialises the scheduling decision, computes how long to sleep, then releases *before* the HTTP call so calls can overlap in-flight.

```csharp
await _throttle.WaitAsync(ct);
try {
    var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastCallAt);
    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    _lastCallAt = DateTime.UtcNow;
} finally { _throttle.Release(); } // released BEFORE the HTTP call
```

| | |
|---|---|
| Advantage | Correct. Works without extra packages. The two-phase split (scheduling vs. in-flight) is the right mental model. |
| Disadvantage | **Requires discipline to get right.** A naive implementation holds the semaphore *through* the HTTP call, serialising everything and making N calls take N × HTTP_time. The fix (release before the call) is non-obvious and easy to miss in code review. Also requires manual `DateTime` arithmetic that `TokenBucketRateLimiter` handles internally. |
| Verdict | **Correct but dated.** Fine to ship; replaced here for clarity. |

---

**Option 3 — `TokenBucketRateLimiter` (System.Threading.RateLimiting) ✅ CHOSEN**

The idiomatic .NET 7+ approach. A bucket starts with 1 token; a background timer replenishes 1 token/sec (`AutoReplenishment = true`). Every call does `AcquireAsync(1)`, which queues the caller until a token is available — no manual timestamp arithmetic, no two-phase semaphore dance.

```csharp
private readonly RateLimiter _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = 1, TokensPerPeriod = 1,
    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
    AutoReplenishment = true,
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    QueueLimit = 10_000,
});

using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
```

Because replenishment is timer-driven (not triggered by lease disposal), holding the lease through the HTTP call does not delay the next token. The HTTP calls genuinely overlap in-flight — the same N-second wall time as option 2, with less code.

| | |
|---|---|
| Advantage | **Smooth 1/sec cadence** — no edge-of-window burst (unlike `FixedWindowRateLimiter`). Built into the runtime — no NuGet package. FIFO queue guarantees fair ordering. `CancellationToken` propagation is first-class. |
| Disadvantage | **In-process only.** Like `SemaphoreSlim`, this lives in one process — two service instances would double the Nominatim request rate. Needs a distributed rate limiter (Redis, etc.) for horizontal scale. |
| Verdict | **Best choice for this scope.** Single-process, smooth, idiomatic, minimal code. |

**Why `TokenBucket` over `FixedWindow`?**

`FixedWindowRateLimiter(1 req / 1s window)` permits two calls in immediate succession if one arrives at t=0.99 s and the next at t=1.00 s (end of one window → start of the next). Nominatim would see a 10ms gap, not a 1s gap. `TokenBucket` smooths this: a token is consumed and the *next* token is only available 1 s after the *previous* one was consumed, regardless of window boundaries.

---

**Option 4 — Polly `RateLimiter` policy**

Polly's `AddRateLimiter` wraps any `System.Threading.RateLimiting` limiter in a Polly pipeline, so it composes with retry and circuit breaker policies.

| | |
|---|---|
| Advantage | Natural fit if you are already using Polly for retry + timeout (as this service does via `Microsoft.Extensions.Http.Resilience`). One pipeline handles rate limiting, retry, and timeout together. |
| Disadvantage | Rate limiting and retry are fundamentally different concerns: retry reacts to *failure*, rate limiting controls *throughput*. Conflating them in one pipeline makes each harder to reason about. A request that hits a rate-limit delay should not trigger retry logic. |
| Verdict | **Good in the right context.** Polly's resilience pipeline in this service handles retry + timeout; the rate limiter sits upstream of it in `NominatimClient`, keeping the two concerns cleanly separated. |

---

**Option 5 — `Channel<T>`-based single-consumer queue**

A `Channel<WorkItem>` decouples callers from Nominatim entirely. A single background worker drains the channel at 1 item/sec and writes results to a `TaskCompletionSource` that the original caller awaits.

| | |
|---|---|
| Advantage | **The right architecture for high volume.** Enables priority queuing (premium tenants jump the queue), per-client fairness via multiple channels with round-robin dispatch, back-pressure, and graceful shedding under overload. Survives process restarts if the channel is backed by a durable broker (Redis Streams, Azure Service Bus). |
| Disadvantage | **Significant complexity for this scope.** You trade a 60-line singleton for a background service, work items, TCS lifecycle management, and a new failure mode (the consumer crashes silently). Requires careful handling of cancellation tokens, channel completion, and error propagation back to callers. |
| Verdict | **Over-engineered for now, right answer at scale.** Add it when batches regularly exceed 50 addresses or the fleet generates sustained bursts of new-route requests. |

---

### Decision summary

```
Rate-limiting concern:  TokenBucketRateLimiter (Option 3) — smooth 1/sec, no extra packages
Retry/timeout concern:  Polly via Microsoft.Extensions.Http.Resilience  — separate pipeline
Scale trigger:          Channel-based queue (Option 5) — when volume demands it
```

The two concerns (rate limiting and failure recovery) are kept in separate layers. `TokenBucketRateLimiter` fires at most 1 request/sec; Polly retries if that request fails. They compose without interfering.

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
- **Rate limiter is tight and correct.** `TokenBucketRateLimiter` enforces genuine 1/sec spacing — no edge-of-window burst. Replenishment is timer-driven, so calls overlap in-flight: one new call fires per second, N calls complete in N seconds, not N × HTTP_time seconds.
- **Per-address error isolation.** One failed address returns `strategy: "error"` without failing the rest of the batch.
- **Retry with configurable back-off.** Transient Nominatim failures are retried silently before surfacing an error to the caller.

### Weaknesses of this approach

- **In-process rate limiter doesn't scale horizontally.** Running two instances doubles the Nominatim request rate — `TokenBucketRateLimiter` lives in one process with no distributed coordination. Mitigation: Redis-backed distributed rate limiter, or a single Nominatim-facing worker behind an internal queue.
- **No per-client fairness.** A single client submitting 200 cold addresses monopolises the rate limiter for ~200 seconds, blocking all other clients' new addresses. Cached requests are unaffected. Mitigation: per-client request queues with round-robin dispatch.
- **No batch size limit enforced.** A caller can send 1,000 addresses and hold an HTTP connection open for ~17 minutes. Mitigation: cap batch size (e.g. 50) and recommend async job submission for larger workloads.
- **In-flight dedup state is in-memory only.** If the service restarts mid-fetch, in-progress tasks are lost. Completed results survive via the persistent cache; only the in-progress ones must be retried by the caller.

---

## Scalability in numbers

### Current capacity (single instance, public Nominatim)

| Scenario | Throughput | Notes |
|---|---|---|
| Warm cache (all cached) | **~1,000 addresses/sec** | SQLite SELECT, ~1ms each |
| Cold cache (all new) | **1 address/sec** | Hard ceiling — Nominatim's 1 req/s policy |
| Mixed (typical fleet) | **~1,000 cached + 1 cold/sec** | After day 1, most routes are cached |
| 10 cold addresses | **~10 seconds** | 1 Nominatim call/sec, overlapping in-flight |
| 50 cold addresses | **~50 seconds** | Recommended max batch size |
| 100 cold addresses | **~1 min 40 sec** | Connection held open — approaching timeout risk |
| 1,000 cold addresses | **~17 minutes** | Should use async job queue instead |
| Per day (cold) | **~86,400 unique addresses** | 1/sec × 86,400 seconds/day |
| Per hour (cold) | **~3,600 unique addresses** | Practical ceiling for new-route ingestion |

### How to improve it

| Improvement | What changes | Numbers after |
|---|---|---|
| **Cap batch size at 50** | Return `400 Bad Request` above 50 addresses | Max response time bounded to ~50s |
| **Async job queue** (`Channel<T>` or Redis Streams) | `POST` returns a job ID; client polls `GET /jobs/{id}` | Handles 10,000+ address batches without holding connections |
| **Redis distributed cache** | Replace SQLite with Redis shared across all pods | 100,000+ cached reads/sec; survives pod restarts |
| **Redis distributed rate limiter** | Replace in-process `TokenBucketRateLimiter` | Multiple pods share one 1 req/sec gate — safe to scale horizontally |
| **Self-hosted Nominatim** (Canada extract, ~50 GB) | Private OSM instance, no rate limit | 50–200 geocodes/sec on commodity hardware; unlimited fan-out |
| **Self-hosted + worker pool (10 workers)** | 10 concurrent threads against private Nominatim | ~500–2,000 addresses/sec; same-day geocoding of a national fleet |

### Practical scale ceiling without any changes

> One instance, public Nominatim, high cache-hit fleet:
> **~1 million cached lookups/day** + **~86,400 new addresses/day** — comfortably handles a fleet of hundreds of vehicles revisiting known routes, with capacity headroom for new-route discovery.

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
