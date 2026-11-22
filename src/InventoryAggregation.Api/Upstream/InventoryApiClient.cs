using System.Net;
using System.Text.Json;
using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// The only class that knows the inventory lives behind HTTP and JSON.
/// Every way a request can fail leaves here as an <see cref="InventoryUnavailableException"/>.
/// Two things are deliberately not wrapped: the caller's own cancellation, and invalid
/// configuration, which is this service's fault and is rejected at startup, not the API's.
/// Retries have already happened by then: they live in <see cref="RetryHandler"/>,
/// which is part of the named HttpClient's pipeline.
/// </summary>
public sealed class InventoryApiClient(IHttpClientFactory httpClientFactory, TimeProvider timeProvider) : IInventoryApiClient
{
    public const string HttpClientName = "InventoryApi";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<InventoryPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        // A fresh HttpClient per call is cheap and correct: the factory pools and
        // recycles the underlying connection handlers.
        var http = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await http.GetAsync($"api/records?page={page}&pageSize={pageSize}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw FailureFor(response, page);
            }

            return await ReadPageAsync(response, page, cancellationToken);
        }
        catch (InventoryUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. That is not a failure of the Inventory API.
            throw;
        }
        catch (HttpRequestException exception)
        {
            // The transport message can contain the upstream host name, which is private.
            // It stays on the inner exception for the logs and out of the caller-facing message.
            throw new InventoryUnavailableException(
                InventoryFailureReason.Unreachable,
                $"Could not reach the Inventory API for page {page}.",
                exception);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            throw new InventoryUnavailableException(
                InventoryFailureReason.Timeout,
                $"The Inventory API did not respond in time for page {page}.",
                exception);
        }
        catch (Exception exception)
        {
            // Nothing specific is expected here. This keeps the promise in the class summary:
            // whatever went wrong while talking to the API, callers handle one exception type.
            throw new InventoryUnavailableException(
                InventoryFailureReason.UpstreamError,
                $"The request to the Inventory API for page {page} failed unexpectedly.",
                exception);
        }
    }

    private InventoryUnavailableException FailureFor(HttpResponseMessage response, int page)
    {
        var status = (int)response.StatusCode;

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new InventoryUnavailableException(
                InventoryFailureReason.Unauthorized,
                $"The Inventory API rejected this service's API key (HTTP {status})."),

            HttpStatusCode.TooManyRequests => new InventoryUnavailableException(
                InventoryFailureReason.RateLimited,
                $"The Inventory API is still rate limiting this service after retries (page {page}).",
                retryAfter: RetryAfter(response)),

            _ => new InventoryUnavailableException(
                InventoryFailureReason.UpstreamError,
                $"The Inventory API returned HTTP {status} for page {page}."),
        };
    }

    /// <summary>Retry-After as a duration, whether the server sent seconds or an HTTP date.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } seconds)
        {
            return seconds;
        }

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// A 200 is not a guarantee. This checks the envelope is structurally complete;
    /// whether the records make sense is the collector's job.
    /// </summary>
    private static async Task<InventoryPage> ReadPageAsync(
        HttpResponseMessage response, int page, CancellationToken cancellationToken)
    {
        PageDto? dto;
        try
        {
            // Read as a stream of bytes (JSON is UTF-8), not as a string: decoding to a string
            // first would trust the Content-Type charset, and an unknown charset throws.
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            dto = await JsonSerializer.DeserializeAsync<PageDto>(body, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InventoryUnavailableException(
                InventoryFailureReason.InvalidData,
                $"The Inventory API returned a body that is not the expected JSON for page {page}.",
                exception);
        }

        if (dto is not { Records: not null, Page: not null, TotalPages: not null, TotalRecords: not null })
        {
            throw new InventoryUnavailableException(
                InventoryFailureReason.InvalidData,
                $"The Inventory API response for page {page} is missing records, page, totalPages or totalRecords.");
        }

        if (dto.Records.Any(record => record is null))
        {
            throw new InventoryUnavailableException(
                InventoryFailureReason.InvalidData,
                $"The Inventory API response for page {page} contains a null record.");
        }

        return new InventoryPage(dto.Records!, dto.Page.Value, dto.TotalPages.Value, dto.TotalRecords.Value);
    }

    private sealed record PageDto(
        List<InventoryPageRecord?>? Records,
        int? Page,
        int? TotalPages,
        int? TotalRecords);
}
