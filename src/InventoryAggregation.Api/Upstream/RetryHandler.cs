using System.Net;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

/// <summary>How the handler waits between tries. Production uses Task.Delay; tests record the waits.</summary>
public delegate Task RetryDelay(TimeSpan delay, CancellationToken cancellationToken);

/// <summary>
/// The only place that retries. Sits in the HttpClient pipeline, so the client
/// above it sees one logical request that either works or finally fails.
///
/// Retried:     transport errors, a try that exceeds the per-attempt timeout, 408, 429, 5xx.
/// Not retried: every other status (400, 401, 403, 404 ...), because asking again cannot change the answer,
///              and cancellation by the caller.
/// </summary>
public sealed class RetryHandler(
    IOptions<InventoryApiOptions> options,
    RetryDelay delay,
    TimeProvider timeProvider,
    ILogger<RetryHandler> logger) : DelegatingHandler
{
    /// <summary>Added to a server-supplied Retry-After so we do not arrive a moment too early.</summary>
    private static readonly TimeSpan RetryAfterPadding = TimeSpan.FromMilliseconds(250);

    private readonly InventoryApiOptions _options = options.Value;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var isLastAttempt = attempt >= _options.MaxAttempts;
            HttpResponseMessage? response = null;
            string failure;

            // Cancels when the caller cancels OR when this one try has taken too long.
            using (var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attemptCancellation.CancelAfter(_options.AttemptTimeout);

                try
                {
                    response = await base.SendAsync(request, attemptCancellation.Token);

                    // Read the whole body here so a connection that drops or stalls mid-body
                    // is covered by the same timeout and retry as the headers.
                    await response.Content.LoadIntoBufferAsync(attemptCancellation.Token);

                    if (!IsTransient(response.StatusCode) || isLastAttempt)
                    {
                        return response;
                    }

                    failure = $"HTTP {(int)response.StatusCode}";
                }
                catch (HttpRequestException exception) when (!isLastAttempt)
                {
                    failure = exception.Message;
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The caller did not cancel, so it was our per-attempt timeout.
                    if (isLastAttempt)
                    {
                        throw new TimeoutException(
                            $"No response within {_options.AttemptTimeout.TotalSeconds:0.#} s on any of {attempt} attempts.",
                            exception);
                    }

                    failure = $"no response within {_options.AttemptTimeout.TotalSeconds:0.#} s";
                }
            }

            var wait = NextDelay(response, attempt);
            response?.Dispose();

            logger.LogWarning(
                "Inventory API attempt {Attempt} of {MaxAttempts} failed ({Failure}); retrying in {DelayMs} ms",
                attempt, _options.MaxAttempts, failure, (int)wait.TotalMilliseconds);

            await delay(wait, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private TimeSpan NextDelay(HttpResponseMessage? response, int attempt)
    {
        TimeSpan wait;

        if (ServerRequestedDelay(response) is { } requested)
        {
            // The server told us when to come back (the live API does this on 429). Believe it.
            wait = (requested > TimeSpan.Zero ? requested : TimeSpan.Zero) + RetryAfterPadding;
        }
        else
        {
            // Exponential backoff: base, 2x base, 4x base ... plus jitter so that several
            // instances that failed together do not all retry at the same instant.
            var backoff = _options.BaseBackoff * Math.Pow(2, attempt - 1);
            var jitter = _options.BaseBackoff * (Random.Shared.NextDouble() / 2);
            wait = backoff + jitter;
        }

        return wait > _options.MaxRetryDelay ? _options.MaxRetryDelay : wait;
    }

    /// <summary>Retry-After can be a number of seconds or an HTTP date.</summary>
    private TimeSpan? ServerRequestedDelay(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;

        if (retryAfter?.Delta is { } seconds)
        {
            return seconds;
        }

        if (retryAfter?.Date is { } date)
        {
            return date - timeProvider.GetUtcNow();
        }

        return null;
    }
}
