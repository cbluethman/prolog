namespace InventoryAggregation.Api.Domain;

/// <summary>
/// One validated inventory record. <see cref="Category"/> is kept exactly as the
/// upstream API sent it; normalising it is a business rule owned by the aggregator.
/// <see cref="LastUpdated"/> is always UTC.
/// </summary>
public sealed record InventoryRecord(
    int Id,
    string Name,
    string Category,
    decimal Price,
    int Quantity,
    DateTime LastUpdated);
