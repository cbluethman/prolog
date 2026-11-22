using InventoryAggregation.Api;
using InventoryAggregation.Api.Domain;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Tests.Fakes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Tests;

public class InventorySummaryServiceTests
{
    private readonly StubCollector _collector = new();
    private readonly ManualTimeProvider _clock = new();

    private readonly StubLifetime _lifetime = new();

    private InventorySummaryService CreateService(TimeSpan? cacheTtl = null, TimeSpan? crawlTimeout = null) => new(
        _collector,
        new InventoryAggregator(),
        Options.Create(new InventoryApiOptions
        {
            CacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5),
            CrawlTimeout = crawlTimeout ?? TimeSpan.FromSeconds(30),
        }),
        _clock,
        _lifetime,
        NullLogger<InventorySummaryService>.Instance);

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

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    public async Task Cached_result_is_reused_right_up_to_the_ttl_and_not_at_it(int ticksFromExpiry, int expectedCollections)
    {
        var service = CreateService();

        await service.GetSummaryAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(ticksFromExpiry);
        await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(expectedCollections, _collector.Calls);
    }

    [Fact]
    public async Task Ttl_is_counted_from_the_latest_collection()
    {
        var service = CreateService();

        await service.GetSummaryAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(6);
        var refreshed = await service.GetSummaryAsync(CancellationToken.None);
        // 10 minutes after the first collection, 4 after the second.
        _clock.Now += TimeSpan.FromMinutes(4);
        var later = await service.GetSummaryAsync(CancellationToken.None);

        Assert.Same(refreshed, later);
        Assert.Equal(2, _collector.Calls);
    }

    [Fact]
    public async Task Caller_arriving_while_an_expired_result_is_being_refreshed_waits_for_the_new_one()
    {
        var service = CreateService();
        var expired = await service.GetSummaryAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(6);
        _collector.Hold = new TaskCompletionSource();
        _collector.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var refreshing = service.GetSummaryAsync(CancellationToken.None);
        await _collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var arrival = service.GetSummaryAsync(CancellationToken.None);
        var answeredEarly = arrival.IsCompleted;
        _collector.Hold.SetResult();

        Assert.False(answeredEarly);
        Assert.Same(await refreshing, await arrival.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotSame(expired, await arrival);
        Assert.Equal(2, _collector.Calls);
    }

    [Fact]
    public async Task Zero_ttl_collects_on_every_call()
    {
        var service = CreateService(cacheTtl: TimeSpan.Zero);

        await service.GetSummaryAsync(CancellationToken.None);
        await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(2, _collector.Calls);
    }

    [Fact]
    public async Task Caller_that_gives_up_does_not_stop_the_crawl()
    {
        var service = CreateService();
        using var impatient = new CancellationTokenSource();
        _collector.Hold = new TaskCompletionSource();

        var abandoned = service.GetSummaryAsync(impatient.Token);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(5)));

        // The crawl the impatient caller started is still the one that answers the next caller.
        var patient = service.GetSummaryAsync(CancellationToken.None);
        _collector.Hold.SetResult();
        var summary = await patient.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(summary);
        Assert.Equal(1, _collector.Calls);
    }

    [Fact]
    public async Task Caller_cancelled_while_waiting_does_not_disturb_the_collection_in_progress()
    {
        var service = CreateService();
        using var impatient = new CancellationTokenSource();
        _collector.Hold = new TaskCompletionSource();

        var collecting = service.GetSummaryAsync(CancellationToken.None);
        var waiting = service.GetSummaryAsync(impatient.Token);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        _collector.Hold.SetResult();
        var summary = await collecting;
        var afterwards = await service.GetSummaryAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(summary, afterwards);
        Assert.Equal(1, _collector.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_failure_and_the_next_caller_tries_again()
    {
        var service = CreateService();
        var failure = new InventoryUnavailableException(InventoryFailureReason.Timeout, "too slow");
        _collector.Hold = new TaskCompletionSource();
        _collector.Failure = failure;
        _collector.OnlyCallThatFails = 1;

        var first = service.GetSummaryAsync(CancellationToken.None);
        var second = service.GetSummaryAsync(CancellationToken.None);
        _collector.Hold.SetResult();

        // One crawl, one failure, delivered to both: the second caller does not queue up a crawl of its own.
        Assert.Same(failure, await Assert.ThrowsAsync<InventoryUnavailableException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InventoryUnavailableException>(() => second));
        Assert.Equal(1, _collector.Calls);

        Assert.NotNull(await service.GetSummaryAsync(CancellationToken.None));
        Assert.Equal(2, _collector.Calls);
    }

    [Fact]
    public async Task Crawl_that_exceeds_its_time_budget_is_a_timeout()
    {
        var service = CreateService(crawlTimeout: TimeSpan.FromSeconds(30));
        _collector.Hold = new TaskCompletionSource();

        var pending = service.GetSummaryAsync(CancellationToken.None);
        await _collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // No real waiting: the budget is measured on the injected clock.
        _clock.Now += TimeSpan.FromSeconds(30);

        var exception = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(InventoryFailureReason.Timeout, exception.Reason);
        Assert.Contains("30 seconds", exception.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task Crawl_that_is_still_inside_its_time_budget_is_left_to_finish()
    {
        var service = CreateService(crawlTimeout: TimeSpan.FromSeconds(30));
        _collector.Hold = new TaskCompletionSource();

        var pending = service.GetSummaryAsync(CancellationToken.None);
        await _collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Now += TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1);
        _collector.Hold.SetResult();

        Assert.NotNull(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Each_crawl_gets_the_whole_time_budget_to_itself()
    {
        var service = CreateService(cacheTtl: TimeSpan.Zero, crawlTimeout: TimeSpan.FromSeconds(30));

        // The first crawl is long over when the second starts 29 seconds in.
        await service.GetSummaryAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromSeconds(29);
        _collector.Hold = new TaskCompletionSource();
        _collector.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = service.GetSummaryAsync(CancellationToken.None);
        await _collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Now += TimeSpan.FromSeconds(29);
        _collector.Hold.SetResult();

        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Finished_crawl_leaves_no_deadline_timer_behind()
    {
        var service = CreateService();

        await service.GetSummaryAsync(CancellationToken.None);

        Assert.Equal(0, _clock.PendingTimers);
    }

    [Fact]
    public async Task Application_shutdown_stops_the_crawl_and_tells_waiting_callers_to_retry()
    {
        var service = CreateService();
        _collector.Hold = new TaskCompletionSource();

        var pending = service.GetSummaryAsync(CancellationToken.None);
        await _lifetime.Stopping.CancelAsync();

        // Not a bare cancellation: the caller did not cancel, and the endpoint turns this into a 503.
        var exception = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(InventoryFailureReason.Unreachable, exception.Reason);
        Assert.Contains("shutting down", exception.Message);
    }

    [Fact]
    public async Task Values_too_large_to_total_are_invalid_data_not_a_crash()
    {
        _collector.Records =
        [
            new(1, "A", "Toys", decimal.MaxValue, 2, _clock.Now.UtcDateTime),
        ];

        var exception = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => CreateService().GetSummaryAsync(CancellationToken.None));

        Assert.Equal(InventoryFailureReason.InvalidData, exception.Reason);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public async Task Metadata_counts_are_zero_for_an_empty_dataset()
    {
        var summary = await CreateService().GetSummaryAsync(CancellationToken.None);

        Assert.Empty(summary.Categories);
        Assert.Equal(0, summary.Metadata.RecordsRetrieved);
        Assert.Equal(0, summary.Metadata.ZeroQuantityExcluded);
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Stopping { get; } = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopping.Cancel();
    }

    private sealed class StubCollector : IInventoryCollector
    {
        public IReadOnlyList<InventoryRecord> Records { get; set; } = [];
        public Exception? Failure { get; set; }

        /// <summary>When set, only this call (counting from 1) throws <see cref="Failure"/>.</summary>
        public int? OnlyCallThatFails { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Completes once a crawl has reached the collector, so its deadline is already running.</summary>
        public TaskCompletionSource Entered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<IReadOnlyList<InventoryRecord>> CollectAllAsync(CancellationToken cancellationToken)
        {
            // Crawls run on the thread pool now, so count safely.
            var call = Interlocked.Increment(ref _calls);
            Entered.TrySetResult();

            if (Hold is not null)
            {
                // A crawl in progress ends early when its caller goes away, like the real one.
                await Hold.Task.WaitAsync(cancellationToken);
            }

            var fails = Failure is not null && (OnlyCallThatFails is null || OnlyCallThatFails == call);
            return fails ? throw Failure! : Records;
        }
    }
}
