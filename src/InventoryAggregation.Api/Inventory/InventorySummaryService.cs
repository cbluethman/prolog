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
/// 10 seconds and the dataset needs 7, so one crawl takes about 10 seconds.
///
/// A crawl is shared, and it does not belong to any one caller. Everyone who asks while
/// it is running awaits the same task and gets the same result or the same failure.
/// A caller that gives up stops waiting, but the crawl carries on, so the next caller
/// finds the answer cached instead of starting again from page 1.
/// </summary>
public sealed class InventorySummaryService(
    IInventoryCollector collector,
    IInventoryAggregator aggregator,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime) : IInventorySummaryService
{
    private readonly InventoryApiOptions _options = options.Value;

    private readonly Lock _lock = new();

    /// <summary>The crawl in progress, if there is one. Guarded by <see cref="_lock"/>.</summary>
    private Task<InventorySummary>? _crawlInProgress;

    /// <summary>Replaced as a whole, so readers never see a half-updated entry.</summary>
    private volatile CacheEntry? _cache;

    public Task<InventorySummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        if (FreshSummary() is { } cached)
        {
            return Task.FromResult(cached);
        }

        Task<InventorySummary> crawl;
        lock (_lock)
        {
            // A crawl may have finished between the check above and taking the lock.
            if (FreshSummary() is { } justCached)
            {
                return Task.FromResult(justCached);
            }

            // Join the crawl in progress, or start one. Task.Run so it starts on its own:
            // not on this caller's request, and never synchronously inside the lock.
            crawl = _crawlInProgress ??= Task.Run(CrawlAsync);
        }

        // The caller's token ends the caller's wait. It does not end the crawl.
        return crawl.WaitAsync(cancellationToken);
    }

    private async Task<InventorySummary> CrawlAsync()
    {
        try
        {
            // The crawl stops for two reasons only: the application is shutting down,
            // or it has used up its whole time budget.
            using var deadline = new CancellationTokenSource(_options.CrawlTimeout, timeProvider);
            using var crawlCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, applicationLifetime.ApplicationStopping);

            IReadOnlyList<InventoryRecord> records;
            try
            {
                records = await collector.CollectAllAsync(crawlCancellation.Token);
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            {
                throw new InventoryUnavailableException(
                    InventoryFailureReason.Timeout,
                    $"The inventory could not be collected within {_options.CrawlTimeout.TotalSeconds:0.#} seconds.",
                    exception);
            }

            var now = timeProvider.GetUtcNow();
            var summary = new InventorySummary(
                aggregator.Aggregate(records),
                new SummaryMetadata(
                    RecordsRetrieved: records.Count,
                    ZeroQuantityExcluded: records.Count(record => !InventoryAggregator.IsAvailable(record)),
                    RetrievedAtUtc: now.UtcDateTime));

            // Cache before clearing the in-progress slot (in finally), so there is no
            // moment at which a new caller sees neither and starts a second crawl.
            _cache = new CacheEntry(summary, ExpiresAt: now + _options.CacheTtl);
            return summary;
        }
        finally
        {
            // Success or failure, this crawl is over. A failure is not cached:
            // the next caller starts a new crawl.
            lock (_lock)
            {
                _crawlInProgress = null;
            }
        }
    }

    private InventorySummary? FreshSummary()
    {
        var entry = _cache;
        return entry is not null && timeProvider.GetUtcNow() < entry.ExpiresAt ? entry.Summary : null;
    }

    private sealed record CacheEntry(InventorySummary Summary, DateTimeOffset ExpiresAt);
}
