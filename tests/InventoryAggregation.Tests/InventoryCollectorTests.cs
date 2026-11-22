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

    public static TheoryData<string, InventoryPageRecord> InvalidRecords => new()
    {
        { "missing id", Record(1) with { Id = null } },
        { "zero id", Record(1) with { Id = 0 } },
        { "missing name", Record(1) with { Name = null } },
        { "blank name", Record(1) with { Name = "  " } },
        { "missing category", Record(1) with { Category = null } },
        { "blank category", Record(1) with { Category = " " } },
        { "missing price", Record(1) with { Price = null } },
        { "negative price", Record(1) with { Price = -0.01m } },
        { "missing quantity", Record(1) with { Quantity = null } },
        { "negative quantity", Record(1) with { Quantity = -1 } },
        { "missing lastUpdated", Record(1) with { LastUpdated = null } },
        { "unreadable lastUpdated", Record(1) with { LastUpdated = "yesterday" } },
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task Invalid_record_fails_the_whole_collection(string reason, InventoryPageRecord invalid)
    {
        Assert.NotEmpty(reason);
        var client = new FakeInventoryApiClient()
            .WithPage(1, totalPages: 1, totalRecords: 2, invalid, Record(2));

        await AssertInvalidDataAsync(client);
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
}
