# Positrace Geocoding API

ASP.NET Core 8 Web API that forward-geocodes Canadian street addresses via the [Nominatim](https://nominatim.org/) public API.

---

## Running locally

**Prerequisites:** .NET 8 SDK

```bash
cd GeocodingApi
dotnet run
```

Swagger UI is available at **http://localhost:5000/swagger** (or the port shown in the console).

The SQLite database file (`geocoding.db`) is created automatically on first startup in the working directory.

---

## POST /api/geocode

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

### `strategy` values

| Value | Meaning |
|---|---|
| `address` | Nominatim matched the normalised street address |
| `postal_code` | Address query returned nothing; result comes from the postal code |
| `not_found` | Neither query returned results |
| `error` | Nominatim was unreachable or returned an HTTP error |

---

## Address normalisation

The service strips the following qualifiers before querying Nominatim, giving the geocoder the cleanest possible input:

| Pattern | Example in → out |
|---|---|
| Dash-prefixed unit (at address start) | `123-12 Main St` → `123 Main St` |
| `Apt` / `Apt.` + identifier | `Apt. 4 456 Yonge St` → `456 Yonge St` |
| `Unit` + identifier | `Unit 201 789 Queen St` → `789 Queen St` |
| `Suite` + identifier | `Suite 300 1000 De La Gauchetière` → `1000 De La Gauchetière` |
| `#` + identifier | `#5 100 Wellington St` → `100 Wellington St` |

**Known limitations**

- The unit identifier is matched as a single whitespace-delimited token (e.g. `Unit 12A` strips `12A`). Multi-token identifiers (`Unit 12 A`) are not handled.
- The dash-prefix rule (`123-12 Main`) is only applied when the pattern appears at the very start of the address string after trimming.
- Qualifiers buried mid-sentence (e.g. `123 Main St, Apt 4, Floor 2`) will have the qualifier stripped but surrounding commas may leave a double-comma, which the cleanup pass collapses.
- French-language equivalents (`App.`, `No`, `Bureau`) are not currently stripped.

---

## Cache key decision

**The cache key is the *normalised* address, not the raw input.**

The cache is keyed on the normalised form (the string actually sent to Nominatim) rather than the raw input that arrived from the caller.

**Why this is the right choice:**

1. **Collapses duplicates before they hit the cache.** `"123 Main St Apt 4"`, `"123 Main St Unit 4"`, and `"123-4 Main St"` all normalise to `"123 Main St"`. With a raw-input key each would be a separate cache miss triggering a redundant Nominatim call; with the normalised key all three share one entry.

2. **Matches the source of truth.** What gets cached is Nominatim's response to the query we actually sent. Keying on the raw input would require re-normalising on every cache lookup to decide whether the stored result applies, which is redundant.

3. **Tradeoff acknowledged.** If normalisation is incorrect for a given input (a known limitation), the wrong cached result could be returned for any raw address that maps to the same bad normalised key. This is acceptable: the failure mode (bad normalisation) is the same whether or not caching is involved, and fixing the normaliser fixes the cache automatically.

---

## Design notes

### In-flight deduplication

`GeocodingService` holds a `ConcurrentDictionary<string, Task<CachedGeocode?>>` keyed on the normalised address. When a Nominatim call is in progress, new concurrent requests for the same address get the same `Task` and `await` it rather than making their own outbound call. Once the task completes (or faults) the entry is removed.

### Rate limiting

`NominatimClient` owns a `SemaphoreSlim(1,1)` and tracks `_lastCallAt`. Before every outbound request it waits for the semaphore, checks the elapsed time since the last call, and delays if less than one second has passed. This serialises all outbound calls and enforces ≥ 1 s between them regardless of how many concurrent geocoding requests are in flight.

### Transient failure handling

HTTP errors from Nominatim (`EnsureSuccessStatusCode`) propagate as exceptions. `GeocodingService` catches them per-address and returns `strategy: "error"` with the exception message, so one bad address does not fail the entire batch. The `_inFlight` task is faulted, so concurrent waiters on the same address also receive the error rather than silently hanging.

### Configuration

All tunable values live in `appsettings.json`:

```json
{
  "ConnectionStrings": { "DefaultConnection": "Data Source=geocoding.db" },
  "Nominatim": {
    "BaseUrl": "https://nominatim.openstreetmap.org/",
    "UserAgent": "Positrace-Geocoding-Service/1.0 (your@email.com)"
  }
}
```
