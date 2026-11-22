namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// The one place that reads a Retry-After header. Both the retry handler (how long to wait
/// before the next try) and the API client (what to tell our own caller) need it.
/// </summary>
internal static class RetryAfterHeader
{
    /// <summary>
    /// The wait the server asked for, whether it sent a number of seconds or an HTTP date.
    /// Null when there is no response or no usable header. Never negative: a date already in
    /// the past, or a negative number of seconds, is zero.
    /// </summary>
    public static TimeSpan? Read(HttpResponseMessage? response, TimeProvider timeProvider)
    {
        var retryAfter = response?.Headers.RetryAfter;

        var requested = retryAfter?.Delta ?? (retryAfter?.Date - timeProvider.GetUtcNow());

        return requested < TimeSpan.Zero ? TimeSpan.Zero : requested;
    }
}
