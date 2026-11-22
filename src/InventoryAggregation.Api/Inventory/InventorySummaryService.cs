using InventoryAggregation.Api.Domain;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Inventory;

/// <summary>What the endpoint returns: the per-category results plus how they were obtained.</summary>
public sealed record InventorySummary(IReadOnlyList<CategorySummary> Categories, SummaryMetadata Metadata);

public sealed record SummaryMetadata(int RecordsRetrieved, int ZeroQuantityExcluded, DateTime RetrievedAtUtc);

public interface IInventorySummaryService
{
    Task<InventorySummary> GetSummaryAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Collect, then aggregate, then remember the answer for a while.
///
/// The cache exists because of how the upstream API behaves: it allows 5 requests per
/// 10 seconds and the dataset needs 7, so one crawl takes about 10 seconds. Without the
/// cache every caller would wait that long, and two concurrent callers would starve each other.
/// </summary>
public sealed class InventorySummaryService(
    IInventoryCollector collector,
    IInventoryAggregator aggregator,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider) : IInventorySummaryService
{
    private readonly TimeSpan _cacheTtl = options.Value.CacheTtl;

    /// <summary>Lets one caller crawl at a time; the others wait and then read what it cached.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Replaced as a whole, so readers never see a half-updated entry.</summary>
    private volatile CacheEntry? _cache;

    public async Task<InventorySummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        if (FreshSummary() is { } cached)
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Someone else may have refreshed while we were waiting for the gate.
            if (FreshSummary() is { } refreshedByAnotherCaller)
            {
                return refreshedByAnotherCaller;
            }

            // If this throws, nothing is cached: the next caller tries again.
            var records = await collector.CollectAllAsync(cancellationToken);

            var now = timeProvider.GetUtcNow();
            var summary = new InventorySummary(
                aggregator.Aggregate(records),
                new SummaryMetadata(
                    RecordsRetrieved: records.Count,
                    ZeroQuantityExcluded: records.Count(record => record.Quantity == 0),
                    RetrievedAtUtc: now.UtcDateTime));

            _cache = new CacheEntry(summary, ExpiresAt: now + _cacheTtl);
            return summary;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private InventorySummary? FreshSummary()
    {
        var entry = _cache;
        return entry is not null && timeProvider.GetUtcNow() < entry.ExpiresAt ? entry.Summary : null;
    }

    private sealed record CacheEntry(InventorySummary Summary, DateTimeOffset ExpiresAt);
}
