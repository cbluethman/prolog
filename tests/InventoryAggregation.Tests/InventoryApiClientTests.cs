using System.Net;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Tests;

/// <summary>
/// Exercises the client through the real registration (named HttpClient, retry handler,
/// options) with only the network replaced, so these tests also cover the DI wiring.
/// </summary>
public class InventoryApiClientTests
{
    private const string OnePage =
        """
        {
          "records": [
            { "id": 1001, "name": "Thermal Receipt Printer", "category": "Electronics",
              "price": 149.99, "quantity": 12, "lastUpdated": "2026-10-15T12:30:00Z" }
          ],
          "page": 1, "pageSize": 20, "totalPages": 8, "totalRecords": 157
        }
        """;

    private readonly ScriptedHttpHandler _network = new();
    private readonly List<TimeSpan> _waits = [];

    private IInventoryApiClient CreateClient(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InventoryApi:BaseUrl"] = "https://inventory.test",
                ["InventoryApi:ApiKey"] = "test-key",
                ["InventoryApi:MaxAttempts"] = "3",
            })
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<RetryDelay>((wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        });
        services.AddInventoryApiClient();
        services.AddHttpClient(InventoryApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _network);

        return services.BuildServiceProvider().GetRequiredService<IInventoryApiClient>();
    }

    private async Task<InventoryUnavailableException> AssertFailsAsync(InventoryFailureReason expected)
    {
        var exception = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => CreateClient().GetPageAsync(1, 20, CancellationToken.None));
        Assert.Equal(expected, exception.Reason);
        return exception;
    }

    [Fact]
    public async Task Requests_the_page_with_the_api_key_header()
    {
        _network.Respond(HttpStatusCode.OK, OnePage);

        await CreateClient().GetPageAsync(3, 25, CancellationToken.None);

        Assert.Equal("https://inventory.test/api/records?page=3&pageSize=25", Assert.Single(_network.RequestUris)?.ToString());
        Assert.Equal("test-key", Assert.Single(_network.ApiKeysSent));
    }

    [Fact]
    public async Task Parses_the_page_envelope_and_records()
    {
        _network.Respond(HttpStatusCode.OK, OnePage);

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(1, page.Page);
        Assert.Equal(8, page.TotalPages);
        Assert.Equal(157, page.TotalRecords);
        Assert.Equal(
            new InventoryPageRecord(1001, "Thermal Receipt Printer", "Electronics", 149.99m, 12, "2026-10-15T12:30:00Z"),
            Assert.Single(page.Records));
    }

    [Fact]
    public async Task Recovers_from_a_rate_limit_then_a_server_error()
    {
        _network
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "10")
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.OK, OnePage);

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(157, page.TotalRecords);
        Assert.Equal(3, _network.RequestCount);
        Assert.Equal(2, _waits.Count);
    }

    [Fact]
    public async Task Server_error_on_every_attempt_is_an_upstream_error()
    {
        _network
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError);

        var exception = await AssertFailsAsync(InventoryFailureReason.UpstreamError);

        Assert.Contains("500", exception.Message);
        Assert.Equal(3, _network.RequestCount);
    }

    [Fact]
    public async Task Rate_limit_on_every_attempt_is_reported_with_the_servers_retry_after()
    {
        _network
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "10")
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "10")
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "7");

        var exception = await AssertFailsAsync(InventoryFailureReason.RateLimited);

        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_api_key_is_reported_as_unauthorized_without_retrying(HttpStatusCode status)
    {
        _network.Respond(status);

        await AssertFailsAsync(InventoryFailureReason.Unauthorized);

        Assert.Equal(1, _network.RequestCount);
    }

    [Fact]
    public async Task Bad_request_is_an_upstream_error_without_retrying()
    {
        _network.Respond(HttpStatusCode.BadRequest);

        await AssertFailsAsync(InventoryFailureReason.UpstreamError);

        Assert.Equal(1, _network.RequestCount);
    }

    [Fact]
    public async Task Transport_failure_on_every_attempt_is_unreachable()
    {
        _network
            .Throw(new HttpRequestException("connection refused"))
            .Throw(new HttpRequestException("connection refused"))
            .Throw(new HttpRequestException("connection refused"));

        await AssertFailsAsync(InventoryFailureReason.Unreachable);
    }

    [Fact]
    public async Task No_answer_on_every_attempt_is_a_timeout()
    {
        _network.Hang().Hang();
        var client = CreateClient(new()
        {
            ["InventoryApi:AttemptTimeout"] = "00:00:00.050",
            ["InventoryApi:MaxAttempts"] = "2",
        });

        var exception = await Assert.ThrowsAsync<InventoryUnavailableException>(
            () => client.GetPageAsync(1, 20, CancellationToken.None));

        Assert.Equal(InventoryFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_an_inventory_failure()
    {
        using var caller = new CancellationTokenSource();
        _network.Hang();

        var pending = CreateClient().GetPageAsync(1, 20, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "records": null, "page": 1, "totalPages": 1, "totalRecords": 0 }""")]
    [InlineData("""{ "records": [], "page": 1, "totalPages": 1 }""")]
    [InlineData("""{ "records": [null], "page": 1, "totalPages": 1, "totalRecords": 1 }""")]
    [InlineData("""{ "records": [{ "id": 1, "price": "not a number" }], "page": 1, "totalPages": 1, "totalRecords": 1 }""")]
    public async Task Success_status_with_an_unusable_body_is_invalid_data(string body)
    {
        _network.Respond(HttpStatusCode.OK, body);

        await AssertFailsAsync(InventoryFailureReason.InvalidData);
    }

    [Theory]
    [InlineData("InventoryApi:ApiKey", "")]
    [InlineData("InventoryApi:BaseUrl", "")]
    [InlineData("InventoryApi:BaseUrl", "not a url")]
    [InlineData("InventoryApi:PageSize", "26")]
    public void Missing_or_invalid_configuration_is_rejected_with_a_clear_error(string key, string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => CreateClient(new() { [key] = value }).GetPageAsync(1, 20, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Contains(key, exception.Message);
    }
}
