using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Tests.Fakes;

/// <summary>An in-memory upstream API: pages are whatever the test says they are.</summary>
public sealed class FakeInventoryApiClient : IInventoryApiClient
{
    private readonly Dictionary<int, InventoryPage> _pages = [];

    /// <summary>When set, every call throws this instead of returning a page.</summary>
    public Exception? Failure { get; set; }

    public List<int> RequestedPages { get; } = [];

    public List<int> RequestedPageSizes { get; } = [];

    /// <summary>Runs after a page has been requested and before it is answered.</summary>
    public Action<int>? AfterRequest { get; set; }

    private readonly Dictionary<int, Exception> _failuresByPage = [];

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

    /// <summary>Only this page fails; the others are served as scripted.</summary>
    public FakeInventoryApiClient FailingOnPage(int page, Exception failure)
    {
        _failuresByPage[page] = failure;
        return this;
    }

    public Task<InventoryPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        // Like the real client: a cancelled caller gets no page.
        cancellationToken.ThrowIfCancellationRequested();

        RequestedPages.Add(page);
        RequestedPageSizes.Add(pageSize);
        AfterRequest?.Invoke(page);

        if (Failure is not null)
        {
            throw Failure;
        }

        if (_failuresByPage.TryGetValue(page, out var failure))
        {
            throw failure;
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
