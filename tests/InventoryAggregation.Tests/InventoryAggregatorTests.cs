using InventoryAggregation.Api.Domain;

namespace InventoryAggregation.Tests;

public class InventoryAggregatorTests
{
    private readonly InventoryAggregator _aggregator = new();

    private static InventoryRecord Record(
        int id, string category, decimal price = 1.00m, int quantity = 1, string lastUpdated = "2026-09-01T00:00:00Z") =>
        new(id, $"Item {id}", category, price, quantity, DateTime.Parse(lastUpdated).ToUniversalTime());

    [Fact]
    public void Empty_input_gives_no_categories()
    {
        Assert.Empty(_aggregator.Aggregate([]));
    }

    [Fact]
    public void Zero_quantity_records_contribute_to_nothing()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Toys", price: 10.00m, quantity: 2, lastUpdated: "2026-09-01T00:00:00Z"),
            // Newest and most expensive, but not available stock.
            Record(2, "Toys", price: 999.99m, quantity: 0, lastUpdated: "2026-09-30T00:00:00Z"),
        ]);

        var toys = Assert.Single(result);
        Assert.Equal(20.00m, toys.TotalInventoryValue);
        Assert.Equal(1, toys.AvailableRecordCount);
        Assert.Equal(1, toys.MostRecentlyUpdated.Id);
        Assert.Equal([1], toys.Items.Select(item => item.Id));
    }

    [Fact]
    public void Category_with_only_zero_quantity_records_is_omitted()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Toys"),
            Record(2, "Discontinued", quantity: 0),
            Record(3, " discontinued ", quantity: 0),
        ]);

        Assert.Equal(["Toys"], result.Select(category => category.Category));
    }

    [Fact]
    public void Category_spelling_variants_are_merged_into_one_group()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Home & Kitchen"),
            Record(2, "  home & kitchen "),
            Record(3, "HOME & KITCHEN"),
            Record(4, "Home  &  Kitchen"),
        ]);

        var category = Assert.Single(result);
        Assert.Equal("Home & Kitchen", category.Category);
        Assert.Equal(4, category.AvailableRecordCount);
    }

    [Fact]
    public void Total_value_is_the_exact_decimal_sum_of_price_times_quantity()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Electronics", price: 149.99m, quantity: 12),
            Record(2, "Electronics", price: 0.10m, quantity: 3),
            Record(3, "Electronics", price: 0.20m, quantity: 1),
        ]);

        // 1799.88 + 0.30 + 0.20. With double arithmetic this would not be exact.
        Assert.Equal(1800.38m, Assert.Single(result).TotalInventoryValue);
    }

    [Fact]
    public void Most_recently_updated_is_the_latest_record_in_the_category()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Outdoor", lastUpdated: "2026-09-01T10:00:00Z"),
            Record(2, "Outdoor", lastUpdated: "2026-09-03T10:00:00Z"),
            Record(3, "Outdoor", lastUpdated: "2026-09-02T10:00:00Z"),
        ]);

        var mostRecent = Assert.Single(result).MostRecentlyUpdated;
        Assert.Equal(2, mostRecent.Id);
        Assert.Equal("Item 2", mostRecent.Name);
        Assert.Equal(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc), mostRecent.LastUpdated);
    }

    [Fact]
    public void Tie_on_last_updated_goes_to_the_lowest_id()
    {
        var result = _aggregator.Aggregate(
        [
            Record(7, "Outdoor", lastUpdated: "2026-09-03T10:00:00Z"),
            Record(4, "Outdoor", lastUpdated: "2026-09-03T10:00:00Z"),
            Record(9, "Outdoor", lastUpdated: "2026-09-01T10:00:00Z"),
        ]);

        Assert.Equal(4, Assert.Single(result).MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Items_are_the_records_the_figures_were_computed_from()
    {
        var result = _aggregator.Aggregate(
        [
            Record(3, "Toys", price: 2.50m, quantity: 4),
            Record(1, "Toys", price: 1.25m, quantity: 2),
            Record(2, "Toys", price: 5.00m, quantity: 0),
        ]);

        var toys = Assert.Single(result);
        Assert.Equal([1, 3], toys.Items.Select(item => item.Id));
        Assert.Equal(toys.AvailableRecordCount, toys.Items.Count);
        Assert.Equal(toys.TotalInventoryValue, toys.Items.Sum(item => item.Price * item.Quantity));
        Assert.Contains(toys.Items, item => item.Id == toys.MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Categories_are_returned_in_alphabetical_order()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "toys"),
            Record(2, "Electronics"),
            Record(3, "OUTDOOR"),
        ]);

        Assert.Equal(["Electronics", "Outdoor", "Toys"], result.Select(category => category.Category));
    }
}
