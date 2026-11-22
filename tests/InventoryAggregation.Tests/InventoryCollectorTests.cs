using InventoryAggregation.Api;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static InventoryAggregation.Tests.Fakes.FakeInventoryApiClient;

namespace InventoryAggregation.Tests;

public class InventoryCollectorTests
{
    private static InventoryCollector CollectorFor(FakeInventoryApiClient client) =>
        new(client, Options.Create(new InventoryApiOptions { PageSize = 2 }), NullLogger<InventoryCollector>.Instance);

    private static async Task<InventoryUnavailableException> AssertInvalidDataAsync(FakeInventoryApiClient client)
    {
        var exception = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => CollectorFor(client).CollectAllAsync(CancellationToken.None));
        Assert.Equal(InventoryFailureReason.InvalidData, exception.Reason);
        return exception;
    }

    [Fact]
    public async Task Collects_every_page_up_to_total_pages()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 3, totalRecords: 5, Record(1), Record(2))
            .WithPage(2, totalPages: 3, totalRecords: 5, Record(3), Record(4))
            .WithPage(3, totalPages: 3, totalRecords: 5, Record(5));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3, 4, 5], records.Select(record => record.Id));
        Assert.Equal([1, 2, 3], client.RequestedPages);
    }

    [Fact]
    public async Task Record_repeated_across_a_page_boundary_is_kept_once()
    {
        // The shape the live API produces: page 2 starts with page 1's last record
        // and carries one row more than the page size.
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 4, Record(1), Record(2))
            .WithPage(2, totalPages: 2, totalRecords: 4, Record(2), Record(3), Record(4));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], records.Select(record => record.Id));
    }

    [Fact]
    public async Task Same_id_with_different_content_is_rejected()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 3, Record(1), Record(2, price: 5.00m))
            .WithPage(2, totalPages: 2, totalRecords: 3, Record(2, price: 6.00m), Record(3));

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains("Record 2", exception.Message);
    }

    [Fact]
    public async Task Fewer_unique_records_than_total_records_is_rejected()
    {
        // A page silently came back short: 200 OK, but a record is missing.
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 4, Record(1), Record(2))
            .WithPage(2, totalPages: 2, totalRecords: 4, Record(3));

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains("Collected 3 unique records", exception.Message);
    }

    [Fact]
    public async Task More_unique_records_than_total_records_is_rejected()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1), Record(2));

        await AssertInvalidDataAsync(client);
    }

    [Fact]
    public async Task Totals_changing_between_pages_is_rejected()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 4, Record(1), Record(2))
            .WithPage(2, totalPages: 2, totalRecords: 3, Record(3));

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains("changed mid-crawl", exception.Message);
    }

    [Fact]
    public async Task Response_for_a_different_page_than_requested_is_rejected()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 4, Record(1), Record(2))
            // Asked for page 2, got page 1 again.
            .WithPage(new InventoryPage([Record(1), Record(2)], Page: 1, TotalPages: 2, TotalRecords: 4), servedForRequest: 2);

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains("Requested page 2", exception.Message);
    }

    [Fact]
    public async Task Absurd_total_pages_is_rejected_without_crawling()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: int.MaxValue, totalRecords: 4, Record(1));

        await AssertInvalidDataAsync(client);

        Assert.Equal([1], client.RequestedPages);
    }

    /// <summary>Each invalid record, with a fragment of the message that names the rule it broke.</summary>
    public static TheoryData<string, InventoryPageRecord> InvalidRecords => new()
    {
        { "non-positive id", Record(1) with { Id = null } },
        { "non-positive id", Record(1) with { Id = 0 } },
        { "non-positive id", Record(1) with { Id = -1 } },
        { "has no name", Record(1) with { Name = null } },
        { "has no name", Record(1) with { Name = "  " } },
        { "has no category", Record(1) with { Category = null } },
        { "has no category", Record(1) with { Category = " " } },
        { "negative price", Record(1) with { Price = null } },
        { "negative price", Record(1) with { Price = -0.01m } },
        { "negative quantity", Record(1) with { Quantity = null } },
        { "negative quantity", Record(1) with { Quantity = -1 } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = null } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "" } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "yesterday" } },
        // Not ISO 8601. A lenient parser accepts all of these, and turns "12:30" into today's date.
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "12:30" } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "Oct 15" } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "10/15/2026" } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "2026-10-15" } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "2026-13-45T12:30:00Z" } },
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task Invalid_record_fails_the_whole_collection(string brokenRule, InventoryPageRecord invalid)
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 2, invalid, Record(2));

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains(brokenRule, exception.Message);
    }

    [Theory]
    [InlineData("2026-10-15T12:30:00Z")]
    [InlineData("2026-10-15T12:30:00.5Z")]
    [InlineData("2026-10-15T12:30:00.1234567Z")]
    [InlineData("2026-10-15T14:30:00+02:00")]
    [InlineData("2026-10-15T12:30:00")]
    public async Task Iso_8601_last_updated_is_accepted_in_each_of_its_forms(string lastUpdated)
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1, lastUpdated: lastUpdated));

        var record = Assert.Single(await CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Equal(new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc), record.LastUpdated.AddTicks(-(record.LastUpdated.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Fact]
    public async Task Oversized_upstream_value_is_cut_short_in_the_error_message()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1) with { LastUpdated = new string('x', 5000) });

        var exception = await AssertInvalidDataAsync(client);

        Assert.InRange(exception.Message.Length, 1, 200);
    }

    [Fact]
    public async Task Zero_quantity_and_zero_price_are_valid()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 2, Record(1, quantity: 0), Record(2, price: 0m));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task Last_updated_is_converted_to_utc()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1, lastUpdated: "2026-10-15T14:30:00+02:00"));

        var record = Assert.Single(await CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Equal(new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc), record.LastUpdated);
        Assert.Equal(DateTimeKind.Utc, record.LastUpdated.Kind);
    }

    [Fact]
    public async Task Empty_dataset_is_valid()
    {
        var client = new FakeInventoryApiClient().WithPage(1, totalPages: 0, totalRecords: 0);

        Assert.Empty(await CollectorFor(client).CollectAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Client_failure_is_not_swallowed()
    {
        var failure = new InventoryUnavailableException(InventoryFailureReason.Unreachable, "down");
        var client = new FakeInventoryApiClient { Failure = failure };

        var thrown = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Every_page_is_requested_with_the_configured_page_size()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 3, Record(1), Record(2))
            .WithPage(2, totalPages: 2, totalRecords: 3, Record(3));

        await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([2, 2], client.RequestedPageSizes);
    }

    [Fact]
    public async Task Single_page_dataset_never_requests_a_second_page()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 2, Record(1), Record(2))
            // Past the end. Asking for it would spend a request and break the count.
            .WithPage(2, totalPages: 1, totalRecords: 2, Record(3));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([1, 2], records.Select(record => record.Id));
        Assert.Equal([1], client.RequestedPages);
    }

    [Fact]
    public async Task Empty_page_in_the_middle_is_accepted_when_the_unique_count_still_matches()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 3, totalRecords: 3, Record(1), Record(2))
            .WithPage(2, totalPages: 3, totalRecords: 3)
            .WithPage(3, totalPages: 3, totalRecords: 3, Record(3));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], records.Select(record => record.Id));
        Assert.Equal([1, 2, 3], client.RequestedPages);
    }

    [Fact]
    public async Task Record_repeated_within_one_page_is_kept_once()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 2, Record(1), Record(1), Record(2));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([1, 2], records.Select(record => record.Id));
    }

    [Fact]
    public async Task Same_id_with_different_content_within_one_page_is_rejected()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1, quantity: 1), Record(1, quantity: 2));

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains("Record 1", exception.Message);
    }

    [Fact]
    public async Task Repeat_that_differs_only_in_how_its_values_are_written_is_the_same_record()
    {
        // Same instant and same amount, spelled differently.
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 1, Record(1, price: 1.5m, lastUpdated: "2026-09-01T00:00:00Z"))
            .WithPage(2, totalPages: 2, totalRecords: 1, Record(1, price: 1.50m, lastUpdated: "2026-09-01T02:00:00+02:00"));

        var record = Assert.Single(await CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Equal(1, record.Id);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(10_001, 0)]
    public async Task Nonsensical_totals_on_the_first_page_are_rejected_without_crawling(int totalPages, int totalRecords)
    {
        var client = new FakeInventoryApiClient().WithPage(1, totalPages, totalRecords);

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains($"totalPages = {totalPages}, totalRecords = {totalRecords}", exception.Message);
        Assert.Equal([1], client.RequestedPages);
    }

    [Fact]
    public async Task Total_pages_changing_between_pages_is_rejected()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 3, Record(1), Record(2))
            .WithPage(2, totalPages: 3, totalRecords: 3, Record(3));

        var exception = await AssertInvalidDataAsync(client);

        Assert.Contains("changed mid-crawl", exception.Message);
    }

    [Fact]
    public async Task Failure_on_a_later_page_fails_the_whole_collection()
    {
        var failure = new InventoryUnavailableException(InventoryFailureReason.RateLimited, "still throttled");
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 3, totalRecords: 5, Record(1), Record(2))
            .WithPage(2, totalPages: 3, totalRecords: 5, Record(3), Record(4))
            .FailingOnPage(3, failure);

        var thrown = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Cancellation_between_pages_stops_the_crawl()
    {
        using var caller = new CancellationTokenSource();
        var client = new FakeInventoryApiClient
        {
            AfterRequest = page =>
            {
                if (page == 2)
                {
                    caller.Cancel();
                }
            },
        };
        client
            .WithPage(1, totalPages: 4, totalRecords: 4, Record(1))
            .WithPage(2, totalPages: 4, totalRecords: 4, Record(2))
            .WithPage(3, totalPages: 4, totalRecords: 4, Record(3))
            .WithPage(4, totalPages: 4, totalRecords: 4, Record(4));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectorFor(client).CollectAllAsync(caller.Token));

        Assert.Equal([1, 2], client.RequestedPages);
    }

    [Fact]
    public async Task Last_updated_without_an_offset_is_taken_as_utc()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1, lastUpdated: "2026-10-15T12:30:00"));

        var record = Assert.Single(await CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Equal(new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc), record.LastUpdated);
        Assert.Equal(DateTimeKind.Utc, record.LastUpdated.Kind);
    }

    [Fact]
    public async Task Records_are_returned_in_id_order_whatever_order_the_pages_use()
    {
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 2, totalRecords: 4, Record(9), Record(4))
            .WithPage(2, totalPages: 2, totalRecords: 4, Record(7), Record(1));

        var records = await CollectorFor(client).CollectAllAsync(CancellationToken.None);

        Assert.Equal([1, 4, 7, 9], records.Select(record => record.Id));
    }

    [Fact]
    public async Task Category_is_passed_on_exactly_as_received()
    {
        // Normalising it is the aggregator's rule, not the collector's.
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 1, Record(1, category: "  home &  KITCHEN "));

        var record = Assert.Single(await CollectorFor(client).CollectAllAsync(CancellationToken.None));

        Assert.Equal("  home &  KITCHEN ", record.Category);
    }
}
