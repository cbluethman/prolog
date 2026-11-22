namespace InventoryAggregation.Api.Inventory;

/// <summary>
/// Fetches one page of inventory. This interface is the seam between the HTTP world
/// and everything else: implementations own transport, retries and JSON; callers see
/// only pages or an <see cref="InventoryUnavailableException"/>.
/// </summary>
public interface IInventoryApiClient
{
    Task<InventoryPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>A structurally complete page: the envelope fields were all present.</summary>
public sealed record InventoryPage(
    IReadOnlyList<InventoryPageRecord> Records,
    int Page,
    int TotalPages,
    int TotalRecords);

/// <summary>
/// A record exactly as received. Every field is nullable because a 200 response
/// is not proof that the fields are there; <see cref="InventoryCollector"/> validates them.
/// </summary>
public sealed record InventoryPageRecord(
    int? Id,
    string? Name,
    string? Category,
    decimal? Price,
    int? Quantity,
    string? LastUpdated);
