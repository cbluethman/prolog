using System.Net;
using InventoryAggregation.Api;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Tests;

public class RetryHandlerTests
{
    private readonly ScriptedHttpHandler _network = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly InventoryApiOptions _options = new()
    {
        MaxAttempts = 4,
        BaseBackoff = TimeSpan.FromMilliseconds(100),
        MaxRetryDelay = TimeSpan.FromSeconds(30),
        AttemptTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>The handler under test, wired to the fake network, recording waits instead of sleeping.</summary>
    private HttpMessageInvoker CreateInvoker()
    {
        RetryDelay recordOnly = (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        };

        var handler = new RetryHandler(
            Options.Create(_options), recordOnly, TimeProvider.System, NullLogger<RetryHandler>.Instance)
        {
            InnerHandler = _network,
        };

        return new HttpMessageInvoker(handler);
    }

    private Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default) =>
        CreateInvoker().SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://inventory.test/api/records"), cancellationToken);

    [Fact]
    public async Task Success_is_returned_without_retrying()
    {
        _network.Respond(HttpStatusCode.OK);

        var response = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _network.RequestCount);
        Assert.Empty(_waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Transient_status_is_retried_until_success(HttpStatusCode transient)
    {
        _network.Respond(transient).Respond(transient).Respond(HttpStatusCode.OK);

        var response = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, _network.RequestCount);
        Assert.Equal(2, _waits.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Permanent_status_is_returned_without_retrying(HttpStatusCode permanent)
    {
        _network.Respond(permanent);

        var response = await SendAsync();

        Assert.Equal(permanent, response.StatusCode);
        Assert.Equal(1, _network.RequestCount);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_returns_the_last_response()
    {
        _network
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.ServiceUnavailable);

        var response = await SendAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, _network.RequestCount);
        Assert.Equal(3, _waits.Count);
    }

    [Fact]
    public async Task Rate_limit_waits_for_the_servers_retry_after()
    {
        _network.Respond(HttpStatusCode.TooManyRequests, retryAfter: "10").Respond(HttpStatusCode.OK);

        await SendAsync();

        var wait = Assert.Single(_waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.5));
    }

    [Fact]
    public async Task Retry_after_given_as_a_date_is_honoured()
    {
        var comeBackAt = DateTimeOffset.UtcNow.AddSeconds(20).ToString("R");
        _network.Respond(HttpStatusCode.ServiceUnavailable, retryAfter: comeBackAt).Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.InRange(Assert.Single(_waits), TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(21));
    }

    [Fact]
    public async Task Retry_after_is_capped_at_the_configured_maximum()
    {
        _network.Respond(HttpStatusCode.TooManyRequests, retryAfter: "3600").Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.Equal(_options.MaxRetryDelay, Assert.Single(_waits));
    }

    [Fact]
    public async Task Backoff_grows_exponentially_when_the_server_gives_no_hint()
    {
        _network
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.OK);

        await SendAsync();

        // Base 100 ms doubling each time, plus up to 50 ms of jitter.
        Assert.Equal(3, _waits.Count);
        Assert.InRange(_waits[0], TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
        Assert.InRange(_waits[1], TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(250));
        Assert.InRange(_waits[2], TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task Transport_error_is_retried()
    {
        _network.Throw(new HttpRequestException("connection reset")).Respond(HttpStatusCode.OK);

        var response = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _network.RequestCount);
    }

    [Fact]
    public async Task Transport_error_on_every_attempt_is_rethrown()
    {
        for (var i = 0; i < _options.MaxAttempts; i++)
        {
            _network.Throw(new HttpRequestException("connection refused"));
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync());

        Assert.Equal(4, _network.RequestCount);
    }

    [Fact]
    public async Task Attempt_that_exceeds_the_timeout_is_retried()
    {
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _network.Hang().Respond(HttpStatusCode.OK);

        var response = await SendAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _network.RequestCount);
    }

    [Fact]
    public async Task Timeout_on_every_attempt_becomes_a_timeout_exception()
    {
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _options.MaxAttempts = 2;
        _network.Hang().Hang();

        await Assert.ThrowsAsync<TimeoutException>(() => SendAsync());

        Assert.Equal(2, _network.RequestCount);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_retried()
    {
        using var caller = new CancellationTokenSource();
        _network.Hang();

        var pending = SendAsync(caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, _network.RequestCount);
        Assert.Empty(_waits);
    }
}
