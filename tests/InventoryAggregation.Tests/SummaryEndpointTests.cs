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
        _factory = TestApp.Create(services =>
        {
            services.RemoveAll<IInventoryApiClient>();
            services.AddSingleton<IInventoryApiClient>(_upstream);
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpResponseMessage Response, JsonElement Json)> GetSummaryAsync()
    {
        var response = await _factory.CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var json = await response.ReadJsonAsync();
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
    public async Task Summary_is_served_at_the_documented_path()
    {
        _upstream.WithPage(1, totalPages: 0, totalRecords: 0);

        // Spelled out, not taken from the constant: clients have this path in their own code.
        var response = await _factory.CreateClient().GetAsync("/api/inventory/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    [InlineData(InventoryFailureReason.Unreachable, HttpStatusCode.ServiceUnavailable, "unreachable")]
    [InlineData(InventoryFailureReason.RateLimited, HttpStatusCode.ServiceUnavailable, "rate limiting")]
    [InlineData(InventoryFailureReason.Timeout, HttpStatusCode.GatewayTimeout, "timed out")]
    [InlineData(InventoryFailureReason.Unauthorized, HttpStatusCode.BadGateway, "credentials")]
    [InlineData(InventoryFailureReason.UpstreamError, HttpStatusCode.BadGateway, "returned an error")]
    [InlineData(InventoryFailureReason.InvalidData, HttpStatusCode.BadGateway, "incomplete or inconsistent data")]
    public async Task Upstream_failure_becomes_a_problem_response(
        InventoryFailureReason reason, HttpStatusCode expected, string titleSays)
    {
        _upstream.Failure = new InventoryUnavailableException(reason, "what went wrong");

        var (response, json) = await GetSummaryAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, json.GetProperty("status").GetInt32());
        Assert.Equal("what went wrong", json.GetProperty("detail").GetString());
        // Three reasons share 502, so the title is what tells a rejected key from bad data.
        Assert.Contains(titleSays, json.GetProperty("title").GetString());
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
    public async Task Unexpected_exception_shows_no_stack_trace_in_development_either()
    {
        // "dotnet run" uses Development, where ASP.NET would otherwise add its developer exception page.
        using var development = _factory.WithWebHostBuilder(host => host.UseEnvironment("Development"));
        _upstream.Failure = new InvalidOperationException("secret internal detail");

        var response = await development.CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret internal detail", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task Upstream_retry_after_far_in_the_future_is_passed_on_as_at_most_an_hour()
    {
        _upstream.Failure = new InventoryUnavailableException(
            InventoryFailureReason.RateLimited, "throttled", retryAfter: TimeSpan.FromDays(40000));

        var (response, _) = await GetSummaryAsync();

        Assert.Equal(TimeSpan.FromHours(1), response.Headers.RetryAfter?.Delta);
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

    [Theory]
    [InlineData(InventoryFailureReason.Unreachable)]
    [InlineData(InventoryFailureReason.RateLimited)]
    public async Task Service_unavailable_suggests_a_default_retry_after_when_the_upstream_gave_none(InventoryFailureReason reason)
    {
        _upstream.Failure = new InventoryUnavailableException(reason, "try later");

        var (response, _) = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Fractional_upstream_retry_after_is_rounded_up_to_whole_seconds()
    {
        _upstream.Failure = new InventoryUnavailableException(
            InventoryFailureReason.RateLimited, "throttled", retryAfter: TimeSpan.FromSeconds(7.2));

        var (response, _) = await GetSummaryAsync();

        // Rounding down would invite the caller back while the upstream is still throttling.
        Assert.Equal(["8"], response.Headers.GetValues("Retry-After"));
    }

    [Fact]
    public async Task Upstream_retry_after_that_has_already_passed_is_passed_on_as_zero_not_replaced_by_the_default()
    {
        _upstream.Failure = new InventoryUnavailableException(
            InventoryFailureReason.RateLimited, "throttled", retryAfter: TimeSpan.Zero);

        var (response, _) = await GetSummaryAsync();

        Assert.Equal(["0"], response.Headers.GetValues("Retry-After"));
    }

    [Theory]
    [InlineData(InventoryFailureReason.Timeout)]
    [InlineData(InventoryFailureReason.Unauthorized)]
    [InlineData(InventoryFailureReason.UpstreamError)]
    [InlineData(InventoryFailureReason.InvalidData)]
    public async Task Failure_that_waiting_will_not_fix_carries_no_retry_after(InventoryFailureReason reason)
    {
        _upstream.Failure = new InventoryUnavailableException(reason, "what went wrong");

        var (response, _) = await GetSummaryAsync();

        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Failed_request_is_not_remembered_once_the_upstream_recovers()
    {
        _upstream.WithPage(1, totalPages: 1, totalRecords: 1, Record(1));
        _upstream.Failure = new InventoryUnavailableException(InventoryFailureReason.Unreachable, "down");
        var (failed, _) = await GetSummaryAsync();

        _upstream.Failure = null;
        var (recovered, json) = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(1, json.GetProperty("categories").GetArrayLength());
    }

    [Fact]
    public async Task Empty_upstream_dataset_is_a_success_with_no_categories()
    {
        _upstream.WithPage(1, totalPages: 0, totalRecords: 0);

        var (response, json) = await GetSummaryAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.GetProperty("categories").GetArrayLength());
        Assert.Equal(0, json.GetProperty("metadata").GetProperty("recordsRetrieved").GetInt32());
    }

    [Fact]
    public async Task Metadata_says_when_the_data_was_retrieved_in_utc()
    {
        _upstream.WithPage(1, totalPages: 1, totalRecords: 1, Record(1));
        var before = DateTimeOffset.UtcNow;

        var (_, json) = await GetSummaryAsync();

        var raw = json.GetProperty("metadata").GetProperty("retrievedAtUtc").GetString();
        Assert.EndsWith("Z", raw);
        Assert.InRange(DateTimeOffset.Parse(raw!), before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Unknown_route_is_a_404_problem_response()
    {
        var response = await _factory.CreateClient().GetAsync("/api/inventory/nothing-here");
        var json = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, json.GetProperty("status").GetInt32());
        Assert.Empty(_upstream.RequestedPages);
    }

    [Fact]
    public async Task Wrong_method_on_the_summary_route_is_a_405_problem_response()
    {
        var response = await _factory.CreateClient().PostAsync(InventoryEndpoints.SummaryRoute, content: null);
        var json = await response.ReadJsonAsync();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(405, json.GetProperty("status").GetInt32());
        Assert.Empty(_upstream.RequestedPages);
    }
}
