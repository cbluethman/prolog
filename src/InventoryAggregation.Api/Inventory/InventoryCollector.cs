using System.Globalization;
using System.Text.RegularExpressions;
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
public sealed partial class InventoryCollector(
    IInventoryApiClient client,
    IOptions<InventoryApiOptions> options,
    ILogger<InventoryCollector> logger) : IInventoryCollector
{
    /// <summary>Runaway guard: refuse to follow a nonsensical totalPages.</summary>
    private const int MaxPages = 10_000;

    /// <summary>
    /// An ISO 8601 / RFC 3339 date and time: 2026-10-15T12:30:00Z.
    /// Seconds and fractional seconds are optional, "T" and "Z" may be lower case, and the
    /// offset may be Z, +02:00, +0200, +02 or absent (absent means UTC, as the API documents).
    /// </summary>
    [GeneratedRegex(
        @"^(?<date>\d{4}-\d{2}-\d{2})[Tt](?<time>\d{2}:\d{2})(:(?<seconds>\d{2})(\.(?<fraction>\d+))?)?(?<offset>[Zz]|[+-]\d{2}(:?\d{2})?)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601DateTime();

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
            throw InventoryUnavailableException.InvalidData(
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
            throw InventoryUnavailableException.InvalidData($"Requested page {requestedPage} but the response says it is page {page.Page}.");
        }

        if (page.TotalPages is < 0 or > MaxPages || page.TotalRecords < 0)
        {
            throw InventoryUnavailableException.InvalidData(
                $"Page {requestedPage} reports totalPages = {page.TotalPages}, totalRecords = {page.TotalRecords}.");
        }

        if (page.TotalPages != first.TotalPages || page.TotalRecords != first.TotalRecords)
        {
            throw InventoryUnavailableException.InvalidData(
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
                throw InventoryUnavailableException.InvalidData($"Record {record.Id} appears more than once with different content (page {page.Page}).");
            }
        }
    }

    private static InventoryRecord Validate(InventoryPageRecord raw, int page)
    {
        if (raw.Id is not > 0)
        {
            throw InventoryUnavailableException.InvalidData($"A record on page {page} has a missing or non-positive id ({Show(raw.Id)}).");
        }

        var id = raw.Id.Value;

        if (string.IsNullOrWhiteSpace(raw.Name))
        {
            throw InventoryUnavailableException.InvalidData($"Record {id} has no name.");
        }

        if (string.IsNullOrWhiteSpace(raw.Category))
        {
            throw InventoryUnavailableException.InvalidData($"Record {id} has no category.");
        }

        if (raw.Price is not >= 0)
        {
            throw InventoryUnavailableException.InvalidData($"Record {id} has a missing or negative price ({Show(raw.Price)}).");
        }

        if (raw.Quantity is not >= 0)
        {
            throw InventoryUnavailableException.InvalidData($"Record {id} has a missing or negative quantity ({Show(raw.Quantity)}).");
        }

        if (!TryParseIso8601(raw.LastUpdated, out var lastUpdated))
        {
            throw InventoryUnavailableException.InvalidData($"Record {id} has an unreadable lastUpdated ({Show(raw.LastUpdated)}).");
        }

        return new InventoryRecord(id, raw.Name, raw.Category, raw.Price.Value, raw.Quantity.Value, lastUpdated.UtcDateTime);
    }

    /// <summary>
    /// Shape first, then value. The pattern decides what counts as a timestamp at all:
    /// a lenient parser would accept "12:30" and invent today's date, and an invented date
    /// could win "most recently updated". The parse then rejects impossible values (month 13).
    /// </summary>
    private static bool TryParseIso8601(string? text, out DateTimeOffset value)
    {
        value = default;

        if (text is null || Iso8601DateTime().Match(text) is not { Success: true } match)
        {
            return false;
        }

        // Rebuild one canonical form so a single exact format can parse every accepted spelling.
        var seconds = match.Groups["seconds"].Success ? match.Groups["seconds"].Value : "00";

        // .NET keeps 7 fractional digits (100 ns). Anything finer is dropped, not rejected.
        var fraction = match.Groups["fraction"].Value.PadRight(7, '0')[..7];

        var offset = match.Groups["offset"].Value switch
        {
            "" or "Z" or "z" => "+00:00",
            { Length: 3 } hoursOnly => hoursOnly + ":00",
            { Length: 5 } compact => compact.Insert(3, ":"),
            var full => full,
        };

        return DateTimeOffset.TryParseExact(
            $"{match.Groups["date"].Value}T{match.Groups["time"].Value}:{seconds}.{fraction}{offset}",
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    /// <summary>Quotes an upstream value for an error message, cut short so a huge value cannot flood it.</summary>
    private static string Show(object? value)
    {
        const int maxLength = 40;

        if (Convert.ToString(value, CultureInfo.InvariantCulture) is not { Length: > 0 } text)
        {
            return "null";
        }

        return text.Length <= maxLength ? $"'{text}'" : $"'{text[..maxLength]}...'";
    }
}
