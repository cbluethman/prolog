using InventoryAggregation.Api;
using InventoryAggregation.Api.Domain;
using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Tests;

public class InventorySummaryServiceTests
{
    private readonly StubCollector _collector = new();
    private readonly ManualTimeProvider _clock = new();

    private InventorySummaryService CreateService() => new(
        _collector,
        new InventoryAggregator(),
        Options.Create(new InventoryApiOptions { CacheTtl = TimeSpan.FromMinutes(5) }),
        _clock);

    [Fact]
    public async Task Summary_carries_categories_and_metadata()
    {
        _collector.Records =
        [
            new(1, "A", "Toys", 2.00m, 3, _clock.Now.UtcDateTime),
            new(2, "B", "Toys", 9.00m, 0, _clock.Now.UtcDateTime),
        ];

        var summary = await CreateService().GetSummaryAsync(CancellationToken.None);

        Assert.Equal(6.00m, Assert.Single(summary.Categories).TotalInventoryValue);
        Assert.Equal(2, summary.Metadata.RecordsRetrieved);
        Assert.Equal(1, summary.Metadata.ZeroQuantityExcluded);
        Assert.Equal(_clock.Now.UtcDateTime, summary.Metadata.RetrievedAtUtc);
    }

    [Fact]
    public async Task Second_call_within_the_ttl_reuses_the_first_result()
    {
        var service = CreateService();

        var first = await service.GetSummaryAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(4);
        var second = await service.GetSummaryAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, _collector.Calls);
    }

    [Fact]
    public async Task Call_after_the_ttl_collects_again()
    {
        var service = CreateService();

        await service.GetSummaryAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(6);
        await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(2, _collector.Calls);
    }

    [Fact]
    public async Task Failure_is_not_cached()
    {
        var service = CreateService();
        _collector.Failure = new InventoryUnavailableException(InventoryFailureReason.Unreachable, "down");

        await Assert.ThrowsAsync<InventoryUnavailableException>(() => service.GetSummaryAsync(CancellationToken.None));

        _collector.Failure = null;
        await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(2, _collector.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_collection()
    {
        var service = CreateService();
        _collector.Hold = new TaskCompletionSource();

        var callers = Enumerable.Range(0, 5)
            .Select(_ => service.GetSummaryAsync(CancellationToken.None))
            .ToArray();
        _collector.Hold.SetResult();
        var summaries = await Task.WhenAll(callers);

        Assert.Equal(1, _collector.Calls);
        Assert.All(summaries, summary => Assert.Same(summaries[0], summary));
    }

    private sealed class StubCollector : IInventoryCollector
    {
        public IReadOnlyList<InventoryRecord> Records { get; set; } = [];
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public int Calls { get; private set; }

        public async Task<IReadOnlyList<InventoryRecord>> CollectAllAsync(CancellationToken cancellationToken)
        {
            Calls++;

            if (Hold is not null)
            {
                await Hold.Task;
            }

            return Failure is null ? Records : throw Failure;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
