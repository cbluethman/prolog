namespace InventoryAggregation.Api.Domain;

/// <summary>The aggregated figures for one category, plus the records they were computed from.</summary>
public sealed record CategorySummary(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    RecordReference MostRecentlyUpdated,
    IReadOnlyList<CategoryItem> Items);

public sealed record RecordReference(int Id, string Name, DateTime LastUpdated);

public sealed record CategoryItem(int Id, string Name, decimal Price, int Quantity, DateTime LastUpdated);
