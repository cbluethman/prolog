using System.Globalization;
using System.Text.Json;
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
    public void Negative_quantity_is_not_available_stock_either()
    {
        // The collector rejects negative quantities, but the rule must hold without it.
        var result = _aggregator.Aggregate(
        [
            Record(1, "Toys", price: 10.00m, quantity: 2),
            Record(2, "Toys", price: 10.00m, quantity: -5),
        ]);

        var toys = Assert.Single(result);
        Assert.Equal(20.00m, toys.TotalInventoryValue);
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
    public void Most_recently_updated_is_decided_by_the_time_of_day_too()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Outdoor", lastUpdated: "2026-09-03T08:00:00Z"),
            Record(2, "Outdoor", lastUpdated: "2026-09-03T17:30:00Z"),
            Record(3, "Outdoor", lastUpdated: "2026-09-03T17:29:59Z"),
        ]);

        // Same day for all three, so this is not a tie for the lowest id to settle.
        Assert.Equal(2, Assert.Single(result).MostRecentlyUpdated.Id);
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

    [Theory]
    [InlineData("Home\t&\tKitchen")]
    [InlineData("Home\n&\r\nKitchen")]
    [InlineData("\tHome & Kitchen\r\n")]
    [InlineData("Home & Kitchen")]
    public void Tabs_line_breaks_and_non_breaking_spaces_count_as_spacing(string variant)
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Home & Kitchen"),
            Record(2, variant),
        ]);

        var category = Assert.Single(result);
        Assert.Equal("Home & Kitchen", category.Category);
        Assert.Equal(2, category.AvailableRecordCount);
    }

    [Fact]
    public void Categories_that_differ_by_more_than_spacing_and_case_stay_separate()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Home & Kitchen"),
            // No space to collapse: a different string, so a different category.
            Record(2, "Home&Kitchen"),
            Record(3, "Home and Kitchen"),
            Record(4, "Home"),
        ]);

        Assert.Equal(4, result.Count);
        Assert.All(result, category => Assert.Equal(1, category.AvailableRecordCount));
    }

    [Fact]
    public void Grouping_does_not_depend_on_the_culture_of_the_machine()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // In Turkish, upper-case I and lower-case i are not a case pair.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            var result = _aggregator.Aggregate(
            [
                Record(1, "KITCHEN"),
                Record(2, "kitchen"),
            ]);

            var category = Assert.Single(result);
            Assert.Equal("Kitchen", category.Category);
            Assert.Equal(2, category.AvailableRecordCount);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Each_category_is_summarised_from_its_own_records_only()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Toys", price: 2.00m, quantity: 3, lastUpdated: "2026-09-05T00:00:00Z"),
            Record(2, "Outdoor", price: 10.00m, quantity: 1, lastUpdated: "2026-09-09T00:00:00Z"),
            Record(3, "toys", price: 0.50m, quantity: 4, lastUpdated: "2026-09-01T00:00:00Z"),
            Record(4, "Outdoor", price: 7.25m, quantity: 2, lastUpdated: "2026-09-02T00:00:00Z"),
        ]);

        Assert.Equal(2, result.Count);

        var outdoor = result[0];
        Assert.Equal(24.50m, outdoor.TotalInventoryValue);
        Assert.Equal(2, outdoor.MostRecentlyUpdated.Id);
        Assert.Equal([2, 4], outdoor.Items.Select(item => item.Id));

        var toys = result[1];
        Assert.Equal(8.00m, toys.TotalInventoryValue);
        Assert.Equal(1, toys.MostRecentlyUpdated.Id);
        Assert.Equal([1, 3], toys.Items.Select(item => item.Id));
    }

    [Fact]
    public void Result_does_not_depend_on_the_order_of_the_input()
    {
        InventoryRecord[] records =
        [
            Record(5, "Toys", price: 2.00m, quantity: 3, lastUpdated: "2026-09-05T00:00:00Z"),
            Record(2, " OUTDOOR", price: 10.00m, quantity: 1, lastUpdated: "2026-09-09T00:00:00Z"),
            Record(9, "toys", price: 0.50m, quantity: 4, lastUpdated: "2026-09-05T00:00:00Z"),
            Record(4, "Outdoor", price: 7.25m, quantity: 2, lastUpdated: "2026-09-09T00:00:00Z"),
            Record(7, "Electronics", price: 99.99m, quantity: 0),
            Record(1, "electronics", price: 149.99m, quantity: 12),
        ];
        var expected = JsonSerializer.Serialize(_aggregator.Aggregate(records));

        var reversed = JsonSerializer.Serialize(_aggregator.Aggregate(records.Reverse()));
        var byId = JsonSerializer.Serialize(_aggregator.Aggregate(records.OrderBy(record => record.Id)));

        Assert.Equal(expected, reversed);
        Assert.Equal(expected, byId);
    }

    [Fact]
    public void Input_is_left_as_it_was_given()
    {
        List<InventoryRecord> records =
        [
            Record(3, " toys ", quantity: 2),
            Record(1, "Toys", quantity: 0),
            Record(2, "TOYS"),
        ];
        var before = records.ToList();

        _aggregator.Aggregate(records);

        Assert.Equal(before, records);
    }

    [Fact]
    public void Fractions_of_a_cent_are_summed_without_rounding()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Hardware", price: 0.333m, quantity: 3),
            Record(2, "Hardware", price: 0.0001m, quantity: 1),
        ]);

        Assert.Equal(0.9991m, Assert.Single(result).TotalInventoryValue);
    }

    [Fact]
    public void Items_carry_every_field_of_the_record_unchanged()
    {
        var lastUpdated = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc);

        var result = _aggregator.Aggregate(
        [
            new InventoryRecord(1001, "Thermal Receipt Printer", " electronics ", 149.99m, 12, lastUpdated),
        ]);

        Assert.Equal(
            new CategoryItem(1001, "Thermal Receipt Printer", 149.99m, 12, lastUpdated),
            Assert.Single(Assert.Single(result).Items));
    }

    [Fact]
    public void Category_order_does_not_depend_on_the_culture_of_the_machine()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Czech sorts "ch" as one letter that comes after "h".
            CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");

            var result = _aggregator.Aggregate(
            [
                Record(1, "Hats"),
                Record(2, "Chairs"),
                Record(3, "Electronics"),
            ]);

            Assert.Equal(["Chairs", "Electronics", "Hats"], result.Select(category => category.Category));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
