# Inventory Aggregation Service

A small .NET 10 service that reads every record from the paginated Inventory API, aggregates the records by category, and serves the result from one endpoint:

```
GET /api/inventory/summary
```

## Setup

Prerequisite: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Nothing else.

### 1. Configure the API key and base URL

Neither value is in this repository. Supply both from the assignment email using **either** option.

User secrets (stored in your user profile, outside the repo):

```bash
dotnet user-secrets --project src/InventoryAggregation.Api set "InventoryApi:BaseUrl" "<base url>"
dotnet user-secrets --project src/InventoryAggregation.Api set "InventoryApi:ApiKey"  "<api key>"
```

Or environment variables:

```bash
export InventoryApi__BaseUrl="<base url>"     # PowerShell: $env:InventoryApi__BaseUrl = "<base url>"
export InventoryApi__ApiKey="<api key>"       # PowerShell: $env:InventoryApi__ApiKey  = "<api key>"
```

User secrets are only loaded in the `Development` environment, which is what `dotnet run` uses by default. If either value is missing or malformed (a base URL that is not `https`, a key with a trailing newline or a non-ASCII character) the app refuses to start and says which one.

### 2. Run

```bash
dotnet run --project src/InventoryAggregation.Api
```

It listens on `http://localhost:5211`.

### 3. Call the endpoint

```bash
curl http://localhost:5211/api/inventory/summary
```

The first call takes about 11 seconds (see [rate limiting](#what-the-inventory-api-actually-does)). Calls in the next five minutes are answered from memory. If the client gives up before then, the crawl still finishes, so the next call is answered from memory too.

### 4. Run the tests

```bash
dotnet test
```

The tests need no API key and make no network calls.

## Response

```json
{
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 18253.18,
      "availableRecordCount": 25,
      "mostRecentlyUpdated": { "id": 1037, "name": "Barcode Scanner MK-1037", "lastUpdated": "2026-09-23T17:05:00Z" },
      "items": [
        { "id": 1016, "name": "Label Printer MK-1016", "price": 53.74, "quantity": 14, "lastUpdated": "2026-09-20T01:10:00Z" }
      ]
    }
  ],
  "metadata": { "recordsRetrieved": 157, "zeroQuantityExcluded": 21, "retrievedAtUtc": "2026-11-22T19:26:02.41Z" }
}
```

This is the shape from the brief with one addition: `metadata`. It lets a caller see how many records the figures rest on and how old the snapshot is, which matters because results are cached.

Against the current dataset the service returns:

| Category | Available records | Total inventory value | Most recently updated |
|---|---|---|---|
| Electronics | 25 | 18253.18 | 1037 |
| Home & Kitchen | 27 | 17151.51 | 1019 |
| Office Supplies | 30 | 15996.70 | 1091 |
| Outdoor | 27 | 17822.93 | 1073 |
| Toys | 27 | 15879.52 | 1055 |

### When the upstream API fails

The endpoint never returns partial figures, and every failure is `application/problem+json` with no stack trace:

| Status | When |
|---|---|
| 502 Bad Gateway | The Inventory API answered with something unusable: an error status after retries, a rejected API key, malformed JSON, or data that fails the completeness and consistency checks |
| 503 Service Unavailable (with `Retry-After`) | The Inventory API could not be reached, was still rate limiting after retries, or this service is shutting down mid-crawl |
| 504 Gateway Timeout | The Inventory API did not answer in time, or the whole crawl exceeded its 45 second budget |
| 500 | Anything unforeseen: a bug in this service |

## What the Inventory API actually does

I probed the API before designing anything. These are the behaviours I found and what the code does about each.

| Observed | Handling | Where |
|---|---|---|
| Rate limit of 5 requests per 10 seconds, then `429` with `Retry-After`. The dataset needs 7 requests at the maximum page size, so every full crawl is throttled once | Wait for the server's `Retry-After`, then retry. Fetch pages sequentially. Cache the result | `RetryHandler`, `InventoryCollector`, `InventorySummaryService` |
| Every even-numbered page begins with the last record of the previous page and returns `pageSize + 1` rows: 160 rows for 157 records | De-duplicate by `id`. Never count rows, and never stop on a short page | `InventoryCollector` |
| `totalRecords` (157) equals the number of unique ids | Completeness check: unique ids collected must equal `totalRecords`, otherwise fail | `InventoryCollector` |
| Each category is spelled six ways: `"Toys"`, `" Toys"`, `"TOYS"`, `"  toys "` and so on | Group on a trimmed, whitespace-collapsed, case-insensitive key | `InventoryAggregator` |
| A `Discontinued` category whose 7 records all have quantity 0 | Falls out naturally: see decisions below | `InventoryAggregator` |
| A page past the end is `200` with no records; a bad `page` or `pageSize` is `400`; a bad key is `401` | Only statuses that can change on a second try are retried. `400`, `401`, `403`, `404` are not | `RetryHandler` |

I did not see a `500` in roughly 70 requests. The brief says they happen, so they are retried with backoff, but that path is proven only by unit tests.

## Architecture

```
GET /api/inventory/summary
        |
InventoryEndpoints           maps failures to 502 / 503 / 504 problem+json
        |
InventorySummaryService      collect -> aggregate -> cache (one shared crawl at a time)
        |                                   |
InventoryCollector                  InventoryAggregator
  pagination, de-dup,                 pure function: filter, group, sum
  validation, completeness            (Domain/, no I/O)
        |
IInventoryApiClient  --------------  the seam: nothing above this line knows HTTP exists
        |
InventoryApiClient           one page per call; turns every failure into InventoryUnavailableException
        |
RetryHandler                 DelegatingHandler in the named HttpClient's pipeline
        |
HttpClient from IHttpClientFactory
```

Why the pieces are split where they are:

- **`InventoryAggregator` is a pure function over `InventoryRecord`s.** The business rules are the part most likely to change, so they have no dependencies and are tested with plain in-memory lists.
- **`InventoryCollector` owns "is this dataset complete and consistent?"** and depends only on the `IInventoryApiClient` interface. It is tested against a fake that can serve overlapping, short, contradictory or invalid pages.
- **`InventoryApiClient` owns HTTP and JSON**, and only that. It answers "did I get a structurally complete page?" and reports every failure as one exception type with a reason, so nothing above it handles `HttpRequestException` or status codes.
- **`RetryHandler` is the only place that retries.** Putting it in the `HttpClient` pipeline means the client sees one logical request. It is hand-written, about 150 lines, rather than Polly or `Microsoft.Extensions.Http.Resilience`: the policy is small enough to read in full, and I would rather own every line of the resilience behaviour in an exercise that is about exactly that. In production I would use the library.
- **`InventorySummaryService` owns the cache and the crawl in progress.** It is the only stateful class. A crawl is one shared task that every concurrent caller awaits. It runs on the application's lifetime and its own 45 second budget, not on the request that happened to start it: a caller that disconnects stops waiting, but the crawl finishes and is cached. Concurrent callers also share a failure instead of each queuing a crawl of their own.

## Decisions I made where the brief left it open

- **Category normalisation.** Categories that differ only by case or whitespace are the same category. The display name is the title-cased key, so `"  home & kitchen "` is reported as `Home & Kitchen`.
- **`Discontinued` does not appear in the output.** Zero-quantity records are excluded before grouping, as the brief orders the steps, and every `Discontinued` record has quantity 0. A category with no available stock has no figures to report and no "most recently updated" record. The alternative, an entry with zero count and a null record, seemed worse for callers.
- **Fail closed.** If any page fails after retries, any record is invalid, or the unique count does not match `totalRecords`, the endpoint returns an error instead of figures computed from part of the data. A wrong total that looks right is worse than no total.
- **A record is invalid** if it has a missing or non-positive id, a blank name or category, a missing or negative price or quantity, or a `lastUpdated` that is not an ISO 8601 date and time. One invalid record fails the whole request. The date check is by pattern on purpose: a lenient parser reads `"12:30"` as today at 12:30, and an invented date could win "most recently updated". Accepted: `2026-10-15T12:30:00Z`, with seconds and fractional seconds optional, `T` and `Z` in either case, and an offset of `Z`, `+02:00`, `+0200`, `+02` or none (none is taken as UTC, as the API documents). Not accepted: a date with no time, a space instead of `T`, week or ordinal dates. Fractions finer than 100 ns are truncated.
- **Duplicates.** An identical repeat of a record is dropped. The same id with different content is an error, because there is no basis for choosing between them.
- **Ties on `lastUpdated`** go to the lowest id. None exist in the current data.
- **Money** is `decimal` end to end and is not rounded. Prices have two decimal places, so the sums are exact.
- **Ordering.** Categories alphabetical, items by id.
- **Which failures are retried.** Transport errors, a try that exceeds 10 seconds, and `408`, `429`, `500`, `502`, `503`, `504`: up to 5 tries per page. A server-supplied `Retry-After` is honoured (capped at 30 seconds); otherwise exponential backoff from 0.5 seconds with jitter. Other statuses, including `501` and `505`, fail at once.
- **A whole crawl has 45 seconds** (`InventoryApi:CrawlTimeout`). A healthy crawl takes 11. After that the caller gets a 504 instead of waiting out every retry on every page.
- **Redirects are not followed.** `HttpClient` would re-send the `X-Api-Key` header to wherever a redirect pointed. A `3xx` is reported as an upstream error.
- **The base URL must be `https`**, except for localhost, so the key is never sent in clear text. The key itself is rejected at startup if it contains whitespace or a newline, which is what a key read from a file tends to have.
- **Caching.** A successful summary is reused for 5 minutes (`InventoryApi:CacheTtl`). The brief says the dataset is static and a crawl costs 11 seconds of a rate limit shared by every caller. Failures are not cached.
- **The base URL is treated as a secret** as well as the key, because the email asks for both to be kept private. Transport error text, which can contain the host name, goes to the logs and not into responses.

## Known limitations

- **A long rate limit is reported as a timeout.** Five tries with the server's `Retry-After` between them only fit inside the 45 second crawl budget when `Retry-After` is about 10 seconds or less, which is what the live API sends. Against a longer one the caller gets a 504 for the crawl budget, not a 503 carrying the upstream `Retry-After`. Stopping early when the requested wait exceeds the remaining budget would fix that.
- **An empty dataset is believed.** A 200 that says `totalRecords: 0` is internally consistent, so it is accepted and cached as an empty summary for five minutes.
- **A dead upstream takes 45 seconds to report.** If the API accepts connections but never answers, callers wait for the whole crawl budget before getting a 504. Checked against an address that hangs. I never got a refused connection in my environment, so the 503 path is proven only by unit tests.
- **No proactive rate limiting.** The service runs into the 429 and waits, once per crawl, instead of pacing itself to 5 requests per 10 seconds. It works because the API sends an accurate `Retry-After`; it would behave badly against an API that did not.
- **The cache is per process and in memory.** Two instances would each crawl and would compete for the same rate limit. The first request after start or expiry pays the full 11 seconds; there is no background refresh or stale-while-revalidate.
- **Completeness rests on `totalRecords` being true.** If the API dropped a record and also under-reported `totalRecords`, the check would pass. I do not assume ids are contiguous, so I cannot detect that case. Two cheap checks would narrow it and are not implemented: `totalPages` should equal `totalRecords / pageSize` rounded up, and the page after the last should be empty.
- **Validation is all or nothing.** One malformed record makes the whole summary unavailable. That is deliberate, but a real consumer might prefer the valid records plus a list of rejects.
- **An oversized response is reported as unreachable.** A page body over 5 MB is refused while it is being read, which looks like a transport failure: it is retried and ends as a 503, where a 502 for unusable data would be more accurate.
- **Number parsing is more lenient than the documented contract.** A number sent as a JSON string (`"12"`) is accepted.
- **Title-casing category names is naive.** A category such as `TV & AUDIO` would display as `Tv & Audio`.
- **A `Retry-After` longer than the 30 second cap is shortened**, so that retry is likely to be refused again.
- **Separation of HTTP from business logic is by interface and namespace** within one project, not enforced by separate assemblies.
- **Not built:** authentication on this service's own endpoint, HTTPS configuration, health checks, OpenAPI, metrics, a circuit breaker, a container image.
- **A few tests use a real 50 ms timeout** to exercise the per-attempt timeout, which has no injectable clock, so they depend on wall-clock time and could fail on a badly stalled machine.
- **Jitter is lost at the cap.** A backoff that reaches the 30 second maximum is exactly 30 seconds for every instance.
- **A crawl nobody is waiting for still runs to the end**, up to its 45 second budget. That is the price of not tying it to one caller.

## With more time

1. A client-side rate limiter matched to the upstream limit, so the 429 is avoided instead of absorbed.
2. Refresh the cache in the background and serve the previous snapshot meanwhile, so no caller waits 11 seconds.
3. The two extra completeness checks described above.
4. Move `Domain` into its own class library so the compiler enforces that it cannot reference HTTP.
5. Replace `RetryHandler` with `Microsoft.Extensions.Http.Resilience` and keep the same tests as the specification.
