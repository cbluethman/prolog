using System.Net;
using System.Text.Json;
using InventoryAggregation.Api;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static InventoryAggregation.Tests.Fakes.FakeInventoryApiClient;

namespace InventoryAggregation.Tests;

/// <summary>
/// The whole application in memory, with only <see cref="IInventoryApiClient"/> replaced:
/// real routing, real DI wiring, real collector, aggregator, cache and JSON serialisation.
/// </summary>
public class SummaryEndpointTests : IDisposable
{
    private readonly FakeInventoryApiClient _upstream = new();
    private readonly WebApplicationFactory<Program> _factory;

    public SummaryEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("InventoryApi:BaseUrl", "https://inventory.test");
            host.UseSetting("InventoryApi:ApiKey", "test-key");
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInventoryApiClient>();
                services.AddSingleton<IInventoryApiClient>(_upstream);
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpResponseMessage Response, JsonElement Json)> GetSummaryAsync()
    {
        var response = await _factory.CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (response, json);
    }

    [Fact]
    public async Task Returns_the_documented_shape()
    {
        _upstream
            .WithPage(1, totalPages: 2, totalRecords: 4,
                Record(1001, " electronics", price: 149.99m, quantity: 12, lastUpdated: "2026-10-15T12:30:00Z"),
                Record(1002, "Toys", price: 5.00m, quantity: 0))
            .WithPage(2, totalPages: 2, totalRecords: 4,
                Record(1002, "Toys", price: 5.00m, quantity: 0),
                Record(1003, "ELECTRONICS", price: 10.00m, quantity: 1, lastUpdated: "2026-10-01T00:00:00Z"),
                Record(1004, "Toys", price: 2.50m, quantity: 2));

        var (response, json) = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var categories = json.GetProperty("categories");
        Assert.Equal(2, categories.GetArrayLength());

        var electronics = categories[0];
        Assert.Equal("Electronics", electronics.GetProperty("category").GetString());
        Assert.Equal(1809.88m, electronics.GetProperty("totalInventoryValue").GetDecimal());
        Assert.Equal(2, electronics.GetProperty("availableRecordCount").GetInt32());

        var mostRecent = electronics.GetProperty("mostRecentlyUpdated");
        Assert.Equal(1001, mostRecent.GetProperty("id").GetInt32());
        Assert.Equal("Item 1001", mostRecent.GetProperty("name").GetString());
        Assert.Equal("2026-10-15T12:30:00Z", mostRecent.GetProperty("lastUpdated").GetString());

        var items = electronics.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(1001, items[0].GetProperty("id").GetInt32());
        Assert.Equal("Item 1001", items[0].GetProperty("name").GetString());
        Assert.Equal(149.99m, items[0].GetProperty("price").GetDecimal());
        Assert.Equal(12, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal("2026-10-15T12:30:00Z", items[0].GetProperty("lastUpdated").GetString());

        Assert.Equal("Toys", categories[1].GetProperty("category").GetString());
        Assert.Equal(1, categories[1].GetProperty("availableRecordCount").GetInt32());

        var metadata = json.GetProperty("metadata");
        Assert.Equal(4, metadata.GetProperty("recordsRetrieved").GetInt32());
        Assert.Equal(1, metadata.GetProperty("zeroQuantityExcluded").GetInt32());
    }

    [Fact]
    public async Task Second_request_is_served_from_the_cache()
    {
        _upstream.WithPage(1, totalPages: 1, totalRecords: 1, Record(1));

        await GetSummaryAsync();
        await GetSummaryAsync();

        Assert.Equal([1], _upstream.RequestedPages);
    }

    [Theory]
    [InlineData(InventoryFailureReason.Unreachable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(InventoryFailureReason.RateLimited, HttpStatusCode.ServiceUnavailable)]
    [InlineData(InventoryFailureReason.Timeout, HttpStatusCode.GatewayTimeout)]
    [InlineData(InventoryFailureReason.Unauthorized, HttpStatusCode.BadGateway)]
    [InlineData(InventoryFailureReason.UpstreamError, HttpStatusCode.BadGateway)]
    [InlineData(InventoryFailureReason.InvalidData, HttpStatusCode.BadGateway)]
    public async Task Upstream_failure_becomes_a_problem_response(InventoryFailureReason reason, HttpStatusCode expected)
    {
        _upstream.Failure = new InventoryUnavailableException(reason, "what went wrong");

        var (response, json) = await GetSummaryAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, json.GetProperty("status").GetInt32());
        Assert.Equal("what went wrong", json.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Rate_limited_response_passes_on_the_upstream_retry_after()
    {
        _upstream.Failure = new InventoryUnavailableException(
            InventoryFailureReason.RateLimited, "throttled", retryAfter: TimeSpan.FromSeconds(7));

        var (response, _) = await GetSummaryAsync();

        Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Incomplete_upstream_data_is_an_error_not_a_partial_result()
    {
        // totalRecords says 3, only 2 exist.
        _upstream.WithPage(1, totalPages: 1, totalRecords: 3, Record(1), Record(2));

        var (response, json) = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(json.TryGetProperty("categories", out _));
    }

    [Fact]
    public async Task Unexpected_exception_is_a_500_problem_without_a_stack_trace()
    {
        _upstream.Failure = new InvalidOperationException("secret internal detail");

        var response = await _factory.CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("secret internal detail", body);
        Assert.DoesNotContain("   at ", body);
    }
}
