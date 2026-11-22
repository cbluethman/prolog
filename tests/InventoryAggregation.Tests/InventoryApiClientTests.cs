using System.Net;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
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
    private readonly ManualTimeProvider _clock = new();

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
        services.AddSingleton<TimeProvider>(_clock);
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
    [InlineData("[]")]
    [InlineData("""{ "records": [], "page": 1, "totalPages": 1, "totalRecords": 0""")]
    [InlineData("""{ "records": {}, "page": 1, "totalPages": 1, "totalRecords": 0 }""")]
    [InlineData("""{ "records": [], "page": 1.5, "totalPages": 1, "totalRecords": 0 }""")]
    [InlineData("""{ "records": [], "page": 1, "totalPages": 1, "totalRecords": 99999999999 }""")]
    [InlineData("""{ "records": [{ "id": 1, "quantity": 1.5 }], "page": 1, "totalPages": 1, "totalRecords": 1 }""")]
    [InlineData("""{ "records": [{ "id": 1, "lastUpdated": 1760531400 }], "page": 1, "totalPages": 1, "totalRecords": 1 }""")]
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
    [InlineData("InventoryApi:PageSize", "0")]
    [InlineData("InventoryApi:ApiKey", "   ")]
    [InlineData("InventoryApi:MaxAttempts", "0")]
    [InlineData("InventoryApi:AttemptTimeout", "00:00:00")]
    // Absolute URIs that are not something HttpClient can call.
    [InlineData("InventoryApi:BaseUrl", "ftp://inventory.test")]
    [InlineData("InventoryApi:BaseUrl", "localhost:5000")]
    [InlineData("InventoryApi:BaseUrl", "/srv/inventory")]
    // Plain http to another machine would send the API key in clear text.
    [InlineData("InventoryApi:BaseUrl", "http://inventory.test")]
    // What a key read from a file tends to look like. It cannot be sent as a header value.
    [InlineData("InventoryApi:ApiKey", "test-key\n")]
    [InlineData("InventoryApi:ApiKey", "test key")]
    [InlineData("InventoryApi:MaxAttempts", "11")]
    [InlineData("InventoryApi:CrawlTimeout", "00:00:00")]
    // A negative wait would be an infinite Task.Delay or an exception inside the retry loop.
    [InlineData("InventoryApi:MaxRetryDelay", "-00:00:00.001")]
    [InlineData("InventoryApi:BaseBackoff", "-00:00:01")]
    [InlineData("InventoryApi:CacheTtl", "-00:00:01")]
    public async Task Missing_or_invalid_configuration_is_rejected_with_a_clear_error(string key, string value)
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => CreateClient(new() { [key] = value }).GetPageAsync(1, 20, CancellationToken.None));

        Assert.Contains(key, exception.Message);
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000/")]
    public async Task Plain_http_is_accepted_for_a_local_base_url(string baseUrl)
    {
        _network.Respond(HttpStatusCode.OK, OnePage);

        var page = await CreateClient(new() { ["InventoryApi:BaseUrl"] = baseUrl })
            .GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(157, page.TotalRecords);
    }

    [Fact]
    public async Task Redirect_is_reported_as_an_upstream_error_and_never_followed()
    {
        // Following it would re-send X-Api-Key to wherever the redirect points.
        _network.Respond(HttpStatusCode.Found, location: "https://elsewhere.test/api/records");

        var exception = await AssertFailsAsync(InventoryFailureReason.UpstreamError);

        Assert.Contains("302", exception.Message);
        Assert.Equal(1, _network.RequestCount);
    }

    [Fact]
    public void Real_network_handler_is_configured_not_to_follow_redirects()
    {
        // The other tests replace the network handler, so inspect the one production would use.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InventoryApi:BaseUrl"] = "https://inventory.test",
                ["InventoryApi:ApiKey"] = "test-key",
            })
            .Build());
        services.AddInventoryApiClient();
        using var provider = services.BuildServiceProvider();

        var builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
        var clientOptions = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(InventoryApiClient.HttpClientName);
        foreach (var configure in clientOptions.HttpMessageHandlerBuilderActions)
        {
            configure(builder);
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler).AllowAutoRedirect);
    }

    [Fact]
    public async Task Unknown_charset_in_the_content_type_does_not_stop_the_json_being_read()
    {
        _network.Respond(HttpStatusCode.OK, OnePage, contentType: "application/json; charset=bogus-8");

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(157, page.TotalRecords);
    }

    [Fact]
    public async Task Unexpected_exception_from_the_pipeline_is_still_an_inventory_failure()
    {
        var unexpected = new NotSupportedException("something nobody planned for");
        _network.Throw(unexpected);

        var exception = await AssertFailsAsync(InventoryFailureReason.UpstreamError);

        Assert.Same(unexpected, exception.InnerException);
        Assert.DoesNotContain("nobody planned", exception.Message);
    }

    [Theory]
    [InlineData("https://inventory.test/", "https://inventory.test/api/records?page=1&pageSize=20")]
    [InlineData("https://inventory.test/v2", "https://inventory.test/v2/api/records?page=1&pageSize=20")]
    [InlineData("https://inventory.test/v2/", "https://inventory.test/v2/api/records?page=1&pageSize=20")]
    [InlineData("https://inventory.test:8443/tenants/acme/v2", "https://inventory.test:8443/tenants/acme/v2/api/records?page=1&pageSize=20")]
    public async Task Base_url_with_a_path_or_trailing_slash_keeps_its_whole_path(string baseUrl, string expectedRequest)
    {
        _network.Respond(HttpStatusCode.OK, OnePage);

        await CreateClient(new() { ["InventoryApi:BaseUrl"] = baseUrl }).GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(expectedRequest, Assert.Single(_network.RequestUris)?.ToString());
    }

    [Fact]
    public async Task Numbers_sent_as_json_strings_are_read_as_numbers()
    {
        _network.Respond(HttpStatusCode.OK,
            """
            {
              "records": [
                { "id": "1001", "name": "Thermal Receipt Printer", "category": "Electronics",
                  "price": "149.99", "quantity": "12", "lastUpdated": "2026-10-15T12:30:00Z" }
              ],
              "page": "1", "totalPages": "8", "totalRecords": "157"
            }
            """);

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(1, page.Page);
        Assert.Equal(8, page.TotalPages);
        Assert.Equal(157, page.TotalRecords);
        Assert.Equal(
            new InventoryPageRecord(1001, "Thermal Receipt Printer", "Electronics", 149.99m, 12, "2026-10-15T12:30:00Z"),
            Assert.Single(page.Records));
    }

    [Fact]
    public async Task Price_keeps_every_decimal_place_it_was_sent_with()
    {
        // A double would turn this into 0.30000000000000004 or lose the trailing digits.
        _network.Respond(HttpStatusCode.OK,
            """{ "records": [{ "id": 1, "price": 1234567890.123456789 }], "page": 1, "totalPages": 1, "totalRecords": 1 }""");

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(1234567890.123456789m, Assert.Single(page.Records).Price);
    }

    [Fact]
    public async Task Record_with_missing_or_null_fields_is_passed_on_for_the_collector_to_judge()
    {
        _network.Respond(HttpStatusCode.OK,
            """{ "records": [{ "id": 7, "name": null }], "page": 1, "totalPages": 1, "totalRecords": 1 }""");

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(new InventoryPageRecord(7, null, null, null, null, null), Assert.Single(page.Records));
    }

    [Fact]
    public async Task Empty_page_is_a_valid_page()
    {
        _network.Respond(HttpStatusCode.OK, """{ "records": [], "page": 4, "totalPages": 8, "totalRecords": 157 }""");

        var page = await CreateClient().GetPageAsync(4, 20, CancellationToken.None);

        Assert.Empty(page.Records);
        Assert.Equal(4, page.Page);
    }

    [Fact]
    public async Task Rate_limit_without_a_retry_after_reports_none()
    {
        _network
            .Respond(HttpStatusCode.TooManyRequests)
            .Respond(HttpStatusCode.TooManyRequests)
            .Respond(HttpStatusCode.TooManyRequests);

        var exception = await AssertFailsAsync(InventoryFailureReason.RateLimited);

        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public async Task Rate_limit_with_a_retry_after_date_is_reported_with_the_time_left()
    {
        var comeBackAt = _clock.Now.AddMinutes(2).ToString("R");
        _network
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: comeBackAt)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: comeBackAt)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: comeBackAt);

        var exception = await AssertFailsAsync(InventoryFailureReason.RateLimited);

        Assert.Equal(TimeSpan.FromMinutes(2), exception.RetryAfter);
    }

    [Fact]
    public async Task Rate_limit_with_a_retry_after_date_in_the_past_reports_no_wait()
    {
        var alreadyPassed = _clock.Now.AddMinutes(-2).ToString("R");
        _network
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: alreadyPassed)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: alreadyPassed)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: alreadyPassed);

        var exception = await AssertFailsAsync(InventoryFailureReason.RateLimited);

        Assert.Equal(TimeSpan.Zero, exception.RetryAfter);
    }

    [Fact]
    public async Task Unreachable_failure_keeps_the_transport_detail_out_of_its_message()
    {
        var transport = new HttpRequestException("No such host is known. (internal-inventory.corp.example:443)");
        _network.Throw(transport).Throw(transport).Throw(transport);

        var exception = await AssertFailsAsync(InventoryFailureReason.Unreachable);

        // The message can end up in a response body; the inner exception only in the logs.
        Assert.DoesNotContain("internal-inventory", exception.Message);
        Assert.Same(transport, exception.InnerException);
    }

    [Fact]
    public async Task Body_that_breaks_on_every_attempt_is_unreachable()
    {
        _network
            .RespondWithBrokenBody(HttpStatusCode.OK)
            .RespondWithBrokenBody(HttpStatusCode.OK)
            .RespondWithBrokenBody(HttpStatusCode.OK);

        await AssertFailsAsync(InventoryFailureReason.Unreachable);

        Assert.Equal(3, _network.RequestCount);
    }

    [Fact]
    public async Task Body_that_breaks_once_is_recovered_from()
    {
        _network.RespondWithBrokenBody(HttpStatusCode.OK).Respond(HttpStatusCode.OK, OnePage);

        var page = await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(157, page.TotalRecords);
    }

    [Fact]
    public async Task Api_key_is_sent_again_on_every_retry()
    {
        _network
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Throw(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, OnePage);

        await CreateClient().GetPageAsync(1, 20, CancellationToken.None);

        Assert.Equal(["test-key", "test-key", "test-key"], _network.ApiKeysSent);
        Assert.Single(_network.RequestUris.Distinct());
    }
}
