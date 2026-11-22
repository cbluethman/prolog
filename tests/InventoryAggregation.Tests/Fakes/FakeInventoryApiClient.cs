using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Tests.Fakes;

/// <summary>An in-memory upstream API: pages are whatever the test says they are.</summary>
public sealed class FakeInventoryApiClient : IInventoryApiClient
{
    private readonly Dictionary<int, InventoryPage> _pages = [];

    /// <summary>When set, every call throws this instead of returning a page.</summary>
    public Exception? Failure { get; set; }

    public List<int> RequestedPages { get; } = [];

    public FakeInventoryApiClient WithPage(
        int page, int totalPages, int totalRecords, params InventoryPageRecord[] records)
    {
        _pages[page] = new InventoryPage(records, page, totalPages, totalRecords);
        return this;
    }

    public FakeInventoryApiClient WithPage(InventoryPage page, int servedForRequest)
    {
        _pages[servedForRequest] = page;
        return this;
    }

    public Task<InventoryPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        RequestedPages.Add(page);

        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(_pages[page]);
    }

    public static InventoryPageRecord Record(
        int id,
        string category = "Toys",
        decimal price = 1.00m,
        int quantity = 1,
        string lastUpdated = "2026-09-01T00:00:00Z") =>
        new(id, $"Item {id}", category, price, quantity, lastUpdated);
}
