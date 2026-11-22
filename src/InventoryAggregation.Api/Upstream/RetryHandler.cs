using System.Net;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

/// <summary>How the handler waits between tries. Production uses Task.Delay; tests record the waits.</summary>
public delegate Task RetryDelay(TimeSpan delay, CancellationToken cancellationToken);

/// <summary>
/// The only place that retries. Sits in the HttpClient pipeline, so the client
/// above it sees one logical request that either works or finally fails.
///
/// Retried:     transport errors, a try that exceeds the per-attempt timeout, 408, 429, 500, 502, 503, 504.
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

    /// <summary>A page of 25 records is a few kilobytes. Anything near this size is not a page.</summary>
    private const long MaxResponseBytes = 5 * 1024 * 1024;

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
                    // The size limit stops a runaway response from being held in memory.
                    await response.Content.LoadIntoBufferAsync(MaxResponseBytes, attemptCancellation.Token);

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
                        response?.Dispose();
                        throw new TimeoutException(
                            $"No response within {_options.AttemptTimeout.TotalSeconds:0.#} s on any of {attempt} attempts.",
                            exception);
                    }

                    failure = $"no response within {_options.AttemptTimeout.TotalSeconds:0.#} s";
                }
                catch
                {
                    // Leaving for good (last transport error, or the caller cancelled):
                    // headers may have arrived before the body failed, so release the response.
                    response?.Dispose();
                    throw;
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

    /// <summary>Statuses where the same request may succeed later. 501 and 505, for example, never will.</summary>
    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private TimeSpan NextDelay(HttpResponseMessage? response, int attempt)
    {
        double waitMs;

        if (ServerRequestedDelay(response) is { } requested)
        {
            // The server told us when to come back (the live API does this on 429). Believe it.
            waitMs = Math.Max(requested.TotalMilliseconds, 0) + RetryAfterPadding.TotalMilliseconds;
        }
        else
        {
            // Exponential backoff: base, 2x base, 4x base ... plus jitter so that several
            // instances that failed together do not all retry at the same instant.
            var baseMs = _options.BaseBackoff.TotalMilliseconds;
            waitMs = baseMs * Math.Pow(2, attempt - 1) + baseMs * (Random.Shared.NextDouble() / 2);
        }

        // Capped while still a plain number, so a large attempt count cannot overflow TimeSpan.
        return TimeSpan.FromMilliseconds(Math.Min(waitMs, _options.MaxRetryDelay.TotalMilliseconds));
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
