using System.Net;
using System.Text.Json;
using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// The only class that knows the inventory lives behind HTTP and JSON.
/// Every way a request can fail leaves here as an <see cref="InventoryUnavailableException"/>.
/// Retries have already happened by then: they live in <see cref="RetryHandler"/>,
/// which is part of the named HttpClient's pipeline.
/// </summary>
public sealed class InventoryApiClient(IHttpClientFactory httpClientFactory) : IInventoryApiClient
{
    public const string HttpClientName = "InventoryApi";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<InventoryPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        // A fresh HttpClient per call is cheap and correct: the factory pools and
        // recycles the underlying connection handlers.
        var http = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"api/records?page={page}&pageSize={pageSize}", cancellationToken);
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
        catch (Exception exception) when (
            exception is TimeoutException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new InventoryUnavailableException(
                InventoryFailureReason.Timeout,
                $"The Inventory API did not respond in time for page {page}.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw FailureFor(response, page);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParsePage(body, page);
        }
    }

    private static InventoryUnavailableException FailureFor(HttpResponseMessage response, int page)
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
                retryAfter: response.Headers.RetryAfter?.Delta),

            _ => new InventoryUnavailableException(
                InventoryFailureReason.UpstreamError,
                $"The Inventory API returned HTTP {status} for page {page}."),
        };
    }

    /// <summary>
    /// A 200 is not a guarantee. This checks the envelope is structurally complete;
    /// whether the records make sense is the collector's job.
    /// </summary>
    private static InventoryPage ParsePage(string body, int page)
    {
        PageDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PageDto>(body, JsonOptions);
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
