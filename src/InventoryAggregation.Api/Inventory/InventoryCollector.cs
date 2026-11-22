using System.Globalization;
using InventoryAggregation.Api.Domain;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Inventory;

public interface IInventoryCollector
{
    /// <summary>
    /// Returns every inventory record exactly once, or throws
    /// <see cref="InventoryUnavailableException"/>. It never returns a partial dataset.
    /// </summary>
    Task<IReadOnlyList<InventoryRecord>> CollectAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Walks the pages and proves the result is complete and consistent before handing it on.
/// Knows about pagination, not about HTTP.
/// </summary>
public sealed class InventoryCollector(
    IInventoryApiClient client,
    IOptions<InventoryApiOptions> options,
    ILogger<InventoryCollector> logger) : IInventoryCollector
{
    /// <summary>Runaway guard: refuse to follow a nonsensical totalPages.</summary>
    private const int MaxPages = 10_000;

    private readonly int _pageSize = options.Value.PageSize;

    public async Task<IReadOnlyList<InventoryRecord>> CollectAllAsync(CancellationToken cancellationToken)
    {
        var recordsById = new Dictionary<int, InventoryRecord>();

        // Page 1 tells us how big the dataset claims to be. Every later page must agree.
        var first = await client.GetPageAsync(1, _pageSize, cancellationToken);
        CheckEnvelope(first, requestedPage: 1, first);
        AddRecords(first, recordsById);

        // Sequential on purpose: the upstream API allows only 5 requests per 10 seconds,
        // so fetching pages in parallel would only produce more 429s.
        for (var pageNumber = 2; pageNumber <= first.TotalPages; pageNumber++)
        {
            var page = await client.GetPageAsync(pageNumber, _pageSize, cancellationToken);
            CheckEnvelope(page, pageNumber, first);
            AddRecords(page, recordsById);
        }

        // The completeness check. Rows are not counted, unique ids are: the API repeats
        // records across page boundaries, so the row count is larger than the dataset.
        if (recordsById.Count != first.TotalRecords)
        {
            throw Invalid(
                $"Collected {recordsById.Count} unique records from {first.TotalPages} pages, " +
                $"but the API reports totalRecords = {first.TotalRecords}.");
        }

        logger.LogInformation(
            "Collected {RecordCount} unique inventory records from {PageCount} pages",
            recordsById.Count, first.TotalPages);

        return recordsById.Values.OrderBy(record => record.Id).ToList();
    }

    private static void CheckEnvelope(InventoryPage page, int requestedPage, InventoryPage first)
    {
        if (page.Page != requestedPage)
        {
            throw Invalid($"Requested page {requestedPage} but the response says it is page {page.Page}.");
        }

        if (page.TotalPages is < 0 or > MaxPages || page.TotalRecords < 0)
        {
            throw Invalid(
                $"Page {requestedPage} reports totalPages = {page.TotalPages}, totalRecords = {page.TotalRecords}.");
        }

        if (page.TotalPages != first.TotalPages || page.TotalRecords != first.TotalRecords)
        {
            throw Invalid(
                $"Page {requestedPage} reports totalPages = {page.TotalPages}, totalRecords = {page.TotalRecords}, " +
                $"but page 1 reported {first.TotalPages} and {first.TotalRecords}. The dataset changed mid-crawl.");
        }
    }

    private void AddRecords(InventoryPage page, Dictionary<int, InventoryRecord> recordsById)
    {
        foreach (var raw in page.Records)
        {
            var record = Validate(raw, page.Page);

            if (!recordsById.TryGetValue(record.Id, out var existing))
            {
                recordsById.Add(record.Id, record);
            }
            else if (existing == record)
            {
                // Seen on the live API: even pages start with the previous page's last record.
                logger.LogDebug("Ignoring repeated record {RecordId} on page {Page}", record.Id, page.Page);
            }
            else
            {
                // Same id, different content: there is no way to know which one is right.
                throw Invalid($"Record {record.Id} appears more than once with different content (page {page.Page}).");
            }
        }
    }

    private static InventoryRecord Validate(InventoryPageRecord raw, int page)
    {
        if (raw.Id is not > 0)
        {
            throw Invalid($"A record on page {page} has a missing or non-positive id ({Show(raw.Id)}).");
        }

        var id = raw.Id.Value;

        if (string.IsNullOrWhiteSpace(raw.Name))
        {
            throw Invalid($"Record {id} has no name.");
        }

        if (string.IsNullOrWhiteSpace(raw.Category))
        {
            throw Invalid($"Record {id} has no category.");
        }

        if (raw.Price is not >= 0)
        {
            throw Invalid($"Record {id} has a missing or negative price ({Show(raw.Price)}).");
        }

        if (raw.Quantity is not >= 0)
        {
            throw Invalid($"Record {id} has a missing or negative quantity ({Show(raw.Quantity)}).");
        }

        // The API documents ISO 8601 in UTC. A value with no offset is taken to be UTC.
        if (!DateTimeOffset.TryParse(
                raw.LastUpdated,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var lastUpdated))
        {
            throw Invalid($"Record {id} has an unreadable lastUpdated ({Show(raw.LastUpdated)}).");
        }

        return new InventoryRecord(id, raw.Name, raw.Category, raw.Price.Value, raw.Quantity.Value, lastUpdated.UtcDateTime);
    }

    private static string Show(object? value) => value is null ? "null" : $"'{value}'";

    private static InventoryUnavailableException Invalid(string message) =>
        new(InventoryFailureReason.InvalidData, message);
}
