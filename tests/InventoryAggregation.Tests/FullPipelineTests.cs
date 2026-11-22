using System.Net;
using System.Text.Json;
using InventoryAggregation.Api;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InventoryAggregation.Tests;

/// <summary>
/// The whole application including the real API client and retry handler. Only the
/// network and the wait between retries are replaced, so nothing here sleeps.
/// </summary>
public class FullPipelineTests : IDisposable
{
    private const string PageOne =
        """
        {
          "records": [
            { "id": 1001, "name": "Thermal Receipt Printer", "category": " electronics",
              "price": 149.99, "quantity": 12, "lastUpdated": "2026-10-15T12:30:00Z" },
            { "id": 1002, "name": "Wooden Train", "category": "Toys",
              "price": 5.00, "quantity": 0, "lastUpdated": "2026-10-01T00:00:00Z" }
          ],
          "page": 1, "pageSize": 2, "totalPages": 2, "totalRecords": 3
        }
        """;

    // Starts with page one's last record, as the live API does.
    private const string PageTwo =
        """
        {
          "records": [
            { "id": 1002, "name": "Wooden Train", "category": "Toys",
              "price": 5.00, "quantity": 0, "lastUpdated": "2026-10-01T00:00:00Z" },
            { "id": 1003, "name": "USB Cable", "category": "ELECTRONICS",
              "price": 10.00, "quantity": 1, "lastUpdated": "2026-10-01T00:00:00Z" }
          ],
          "page": 2, "pageSize": 2, "totalPages": 2, "totalRecords": 3
        }
        """;

    private readonly ScriptedHttpHandler _network = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly Dictionary<string, string> _settings = new()
    {
        ["InventoryApi:BaseUrl"] = "https://inventory.test",
        ["InventoryApi:ApiKey"] = "test-key",
        ["InventoryApi:PageSize"] = "2",
        ["InventoryApi:MaxAttempts"] = "2",
    };

    private WebApplicationFactory<Program>? _factory;

    private HttpClient CreateClient()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");

            foreach (var (key, value) in _settings)
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<RetryDelay>();
                services.AddSingleton<RetryDelay>((wait, _) =>
                {
                    _waits.Add(wait);
                    return Task.CompletedTask;
                });
                services.AddHttpClient(InventoryApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _network);
            });
        });

        return _factory.CreateClient();
    }

    public void Dispose() => _factory?.Dispose();

    [Fact]
    public async Task Summary_is_built_from_every_page_despite_a_rate_limit_on_the_way()
    {
        _network
            .Respond(HttpStatusCode.OK, PageOne)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "2")
            .Respond(HttpStatusCode.OK, PageTwo);

        var response = await CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var electronics = Assert.Single(json.GetProperty("categories").EnumerateArray());
        Assert.Equal("Electronics", electronics.GetProperty("category").GetString());
        Assert.Equal(1809.88m, electronics.GetProperty("totalInventoryValue").GetDecimal());
        Assert.Equal(2, electronics.GetProperty("availableRecordCount").GetInt32());
        Assert.Equal(3, json.GetProperty("metadata").GetProperty("recordsRetrieved").GetInt32());
        Assert.Equal(1, json.GetProperty("metadata").GetProperty("zeroQuantityExcluded").GetInt32());

        Assert.Equal(
            [
                "https://inventory.test/api/records?page=1&pageSize=2",
                "https://inventory.test/api/records?page=2&pageSize=2",
                "https://inventory.test/api/records?page=2&pageSize=2",
            ],
            _network.RequestUris.Select(uri => uri?.ToString()));
        Assert.All(_network.ApiKeysSent, key => Assert.Equal("test-key", key));
        Assert.Equal(TimeSpan.FromSeconds(2.25), Assert.Single(_waits));
    }

    [Fact]
    public async Task Rate_limit_that_outlasts_the_retries_is_a_503_with_the_upstream_retry_after()
    {
        _network
            .Respond(HttpStatusCode.OK, PageOne)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "2")
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "7");

        var response = await CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter?.Delta);
        // Page one alone is not an answer.
        Assert.DoesNotContain("categories", body);
    }

    [Fact]
    public async Task Rejected_api_key_is_a_502_after_a_single_upstream_request()
    {
        _network.Respond(HttpStatusCode.Unauthorized);

        var response = await CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, _network.RequestCount);
        Assert.DoesNotContain("test-key", body);
    }

    [Fact]
    public async Task Unreachable_upstream_is_a_503_that_does_not_reveal_the_upstream_host()
    {
        _network
            .Throw(new HttpRequestException("No such host is known. (inventory.test:443)"))
            .Throw(new HttpRequestException("No such host is known. (inventory.test:443)"));

        var response = await CreateClient().GetAsync(InventoryEndpoints.SummaryRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("inventory.test", body);
    }

    [Theory]
    [InlineData("InventoryApi:ApiKey")]
    [InlineData("InventoryApi:BaseUrl")]
    public void Application_refuses_to_start_when_a_required_setting_is_missing(string key)
    {
        _settings[key] = "";

        // Only that it fails, not how. The application's own thread fails to start and disposes the
        // host while the test host is still attaching to it, so now and then an
        // ObjectDisposedException surfaced here instead of the OptionsValidationException. The message is
        // checked without that race in InventoryApiClientTests.
        Assert.NotNull(Record.Exception(() => CreateClient()));
    }
}
