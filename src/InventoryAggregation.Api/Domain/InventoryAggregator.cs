using System.Globalization;

namespace InventoryAggregation.Api.Domain;

public interface IInventoryAggregator
{
    IReadOnlyList<CategorySummary> Aggregate(IEnumerable<InventoryRecord> records);
}

/// <summary>
/// Pure business logic: no I/O, no HTTP, no clock. Everything it needs comes in
/// through the argument, so it can be tested with plain in-memory records.
/// </summary>
public sealed class InventoryAggregator : IInventoryAggregator
{
    public IReadOnlyList<CategorySummary> Aggregate(IEnumerable<InventoryRecord> records)
    {
        return records
            // Rule 1: zero-quantity records are not available stock and contribute to nothing.
            .Where(IsAvailable)
            // Rule 2: group by category. The upstream data spells the same category several
            // ways ("Toys", " toys ", "TOYS"), so group on a normalised, case-insensitive key.
            .GroupBy(record => NormalizeCategory(record.Category), StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize(group.Key, group))
            .OrderBy(summary => summary.Category, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The one definition of "available stock". Zero is what the rule names; anything
    /// below zero is not stock either, so this function is safe on unvalidated input.
    /// </summary>
    public static bool IsAvailable(InventoryRecord record) => record.Quantity > 0;

    private static CategorySummary Summarize(string categoryKey, IEnumerable<InventoryRecord> group)
    {
        var records = group.OrderBy(record => record.Id).ToList();

        // Rule 5: most recently updated. A tie goes to the lowest id so the answer is deterministic.
        var mostRecent = records
            .OrderByDescending(record => record.LastUpdated)
            .ThenBy(record => record.Id)
            .First();

        return new CategorySummary(
            Category: ToDisplayName(categoryKey),
            // Rule 3: decimal arithmetic, so money is summed exactly with no rounding.
            TotalInventoryValue: records.Sum(record => record.Price * record.Quantity),
            // Rule 4.
            AvailableRecordCount: records.Count,
            MostRecentlyUpdated: new RecordReference(mostRecent.Id, mostRecent.Name, mostRecent.LastUpdated),
            // Rule 6: the same list the figures above were computed from.
            Items: records
                .Select(record => new CategoryItem(record.Id, record.Name, record.Price, record.Quantity, record.LastUpdated))
                .ToList());
    }

    /// <summary>Trims the ends and collapses runs of inner whitespace to a single space.</summary>
    private static string NormalizeCategory(string category) =>
        string.Join(' ', category.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"home &amp; kitchen" and "HOME &amp; KITCHEN" both display as "Home &amp; Kitchen".</summary>
    private static string ToDisplayName(string categoryKey) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(categoryKey.ToLowerInvariant());
}
