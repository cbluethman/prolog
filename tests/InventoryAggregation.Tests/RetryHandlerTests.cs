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

    private TimeProvider _clock = TimeProvider.System;

    /// <summary>Replaces the recording wait when a test needs the wait itself to do something.</summary>
    private RetryDelay? _delay;

    /// <summary>The handler under test, wired to the fake network, recording waits instead of sleeping.</summary>
    private HttpMessageInvoker CreateInvoker()
    {
        RetryDelay recordOnly = (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        };

        var handler = new RetryHandler(
            Options.Create(_options), _delay ?? recordOnly, _clock, NullLogger<RetryHandler>.Instance)
        {
            InnerHandler = _network,
        };

        return new HttpMessageInvoker(handler);
    }

    private async Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default)
    {
        var sending = CreateInvoker().SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://inventory.test/api/records"), cancellationToken);

        // A handler that never finishes should fail its test, not hang the whole run. Not WaitAsync:
        // its TimeoutException would look like the handler's own to the tests that expect one.
        using var patience = new CancellationTokenSource();
        var gaveUp = Task.Delay(TimeSpan.FromSeconds(10), patience.Token);
        if (await Task.WhenAny(sending, gaveUp) == gaveUp)
        {
            throw new InvalidOperationException("The retry handler did not finish within 10 seconds.");
        }

        await patience.CancelAsync();
        return await sending;
    }

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

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported)]
    public async Task Server_error_that_asking_again_cannot_fix_is_not_retried(HttpStatusCode permanent)
    {
        _network.Respond(permanent);

        var response = await SendAsync();

        Assert.Equal(permanent, response.StatusCode);
        Assert.Equal(1, _network.RequestCount);
    }

    [Fact]
    public async Task Backoff_stays_capped_however_many_attempts_are_configured()
    {
        // 2^59 times the base would overflow TimeSpan if the cap were applied after multiplying.
        _options.MaxAttempts = 60;
        for (var i = 0; i < 60; i++)
        {
            _network.Respond(HttpStatusCode.InternalServerError);
        }

        await SendAsync();

        Assert.Equal(59, _waits.Count);
        Assert.Equal(_options.MaxRetryDelay, _waits.Max());
    }

    [Fact]
    public async Task Response_is_released_when_its_body_fails_on_the_last_attempt()
    {
        _options.MaxAttempts = 1;
        _network.RespondWithBrokenBody(HttpStatusCode.OK);

        await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync());

        Assert.True(Assert.Single(_network.Responses).Disposed);
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

    [Fact]
    public async Task Single_attempt_returns_a_transient_status_without_waiting()
    {
        _options.MaxAttempts = 1;
        _network.Respond(HttpStatusCode.ServiceUnavailable, retryAfter: "10");

        var response = await SendAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, _network.RequestCount);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Single_attempt_rethrows_a_transport_error_without_waiting()
    {
        _options.MaxAttempts = 1;
        _network.Throw(new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync());

        Assert.Equal(1, _network.RequestCount);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Single_attempt_that_times_out_becomes_a_timeout_exception_without_waiting()
    {
        _options.MaxAttempts = 1;
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _network.Hang();

        await Assert.ThrowsAsync<TimeoutException>(() => SendAsync());

        Assert.Equal(1, _network.RequestCount);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Retry_after_of_zero_waits_only_the_safety_padding()
    {
        _network.Respond(HttpStatusCode.TooManyRequests, retryAfter: "0").Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(250), Assert.Single(_waits));
    }

    [Fact]
    public async Task Retry_after_date_in_the_past_waits_only_the_safety_padding()
    {
        var clock = new ManualTimeProvider();
        _clock = clock;
        var anHourAgo = clock.Now.AddHours(-1).ToString("R");
        _network.Respond(HttpStatusCode.ServiceUnavailable, retryAfter: anHourAgo).Respond(HttpStatusCode.OK);

        await SendAsync();

        // Never a negative wait, and never the general backoff either.
        Assert.Equal(TimeSpan.FromMilliseconds(250), Assert.Single(_waits));
    }

    [Fact]
    public async Task Retry_after_date_is_measured_against_the_injected_clock()
    {
        // Years away from the real clock, so only the injected one can give 20 seconds.
        var clock = new ManualTimeProvider { Now = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        _clock = clock;
        var inTwentySeconds = clock.Now.AddSeconds(20).ToString("R");
        _network.Respond(HttpStatusCode.TooManyRequests, retryAfter: inTwentySeconds).Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.Equal(TimeSpan.FromSeconds(20.25), Assert.Single(_waits));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    public async Task Unreadable_retry_after_falls_back_to_backoff(string retryAfter)
    {
        _network.Respond(HttpStatusCode.TooManyRequests, retryAfter: retryAfter).Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.InRange(Assert.Single(_waits), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Backoff_is_capped_at_the_configured_maximum()
    {
        _options.MaxRetryDelay = TimeSpan.FromMilliseconds(150);
        _network
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.InternalServerError)
            .Respond(HttpStatusCode.OK);

        await SendAsync();

        // Uncapped, the second and third waits would be at least 200 ms and 400 ms.
        Assert.Equal(3, _waits.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(150), _waits[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(150), _waits[2]);
    }

    [Fact]
    public async Task Attempt_that_timed_out_backs_off_before_the_next_one()
    {
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _network.Hang().Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.InRange(Assert.Single(_waits), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task Cancellation_during_the_wait_between_attempts_stops_retrying()
    {
        using var caller = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        _delay = async (_, cancellationToken) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        _network.Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.OK);

        var pending = SendAsync(caller.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await caller.CancelAsync();

        // The time limit only turns a hang into a failure if the wait ignores the caller.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _network.RequestCount);
    }

    [Fact]
    public async Task Body_that_breaks_while_being_read_is_retried()
    {
        _network.RespondWithBrokenBody(HttpStatusCode.OK).Respond(HttpStatusCode.OK, "complete");

        var response = await SendAsync();

        Assert.Equal("complete", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, _network.RequestCount);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task Body_that_breaks_on_every_attempt_is_a_transport_error()
    {
        for (var i = 0; i < _options.MaxAttempts; i++)
        {
            _network.RespondWithBrokenBody(HttpStatusCode.OK);
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync());

        Assert.Equal(4, _network.RequestCount);
    }

    [Fact]
    public async Task Body_that_stalls_past_the_timeout_is_retried()
    {
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _network.RespondWithStalledBody(HttpStatusCode.OK).Respond(HttpStatusCode.OK, "complete");

        var response = await SendAsync();

        // Counted first: if the stalled response were handed back, reading it would never end.
        Assert.Equal(2, _network.RequestCount);
        Assert.Equal("complete", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_status_outside_the_transient_six_is_ever_retried()
    {
        HttpStatusCode[] transient =
        [
            HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
        ];
        var retried = new List<int>();

        // 1xx is left out: HttpClient never hands an interim response to a handler.
        foreach (var status in Enumerable.Range(200, 400).Select(code => (HttpStatusCode)code).Except(transient))
        {
            _network.Respond(status);
            var requestsBefore = _network.RequestCount;

            using var response = await SendAsync();

            if (response.StatusCode != status || _network.RequestCount != requestsBefore + 1)
            {
                retried.Add((int)status);
            }
        }

        Assert.Empty(retried);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Backoff_jitter_spreads_retries_over_half_the_base_delay()
    {
        // One first retry per send, so every wait is the 100 ms base plus that send's jitter.
        for (var i = 0; i < 40; i++)
        {
            _network.Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.OK);
            await SendAsync();
        }

        Assert.Equal(40, _waits.Count);
        Assert.All(_waits, wait => Assert.InRange(wait, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150)));
        // Forty random draws from a 50 ms range landing within 5 ms of each other would be about 1 in 10^38.
        Assert.True(_waits.Max() - _waits.Min() > TimeSpan.FromMilliseconds(5), "every retry waited the same time");
    }

    [Fact]
    public async Task Zero_base_backoff_retries_without_waiting()
    {
        _options.BaseBackoff = TimeSpan.Zero;
        _network.Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.InternalServerError).Respond(HttpStatusCode.OK);

        await SendAsync();

        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero], _waits);
    }

    [Fact]
    public async Task Response_that_is_retried_is_released_before_the_next_attempt()
    {
        _network
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.TooManyRequests, retryAfter: "1")
            .Respond(HttpStatusCode.OK);
        var releasedWhenWaiting = new List<bool>();
        _delay = (_, _) =>
        {
            releasedWhenWaiting.Add(_network.Responses[^1].Disposed);
            return Task.CompletedTask;
        };

        await SendAsync();

        Assert.Equal([true, true], releasedWhenWaiting);
    }

    [Fact]
    public async Task Response_is_released_when_its_body_stalls_on_the_last_attempt()
    {
        _options.MaxAttempts = 1;
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _network.RespondWithStalledBody(HttpStatusCode.OK);

        await Assert.ThrowsAsync<TimeoutException>(() => SendAsync());

        Assert.True(Assert.Single(_network.Responses).Disposed);
    }

    [Fact]
    public async Task Response_is_released_when_the_caller_cancels_while_its_body_is_arriving()
    {
        using var caller = new CancellationTokenSource();
        _network.RespondWithStalledBody(HttpStatusCode.OK);

        var pending = SendAsync(caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(Assert.Single(_network.Responses).Disposed);
        Assert.Equal(1, _network.RequestCount);
    }
}
