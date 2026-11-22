namespace InventoryAggregation.Api.Inventory;

public enum InventoryFailureReason
{
    /// <summary>No HTTP response at all: DNS, connection refused, TLS, connection reset.</summary>
    Unreachable,

    /// <summary>The upstream API did not answer in time.</summary>
    Timeout,

    /// <summary>Still being throttled (429) after all retries.</summary>
    RateLimited,

    /// <summary>The upstream API rejected our API key (401/403).</summary>
    Unauthorized,

    /// <summary>Any other non-success status after retries.</summary>
    UpstreamError,

    /// <summary>A success response whose content is malformed, incomplete or inconsistent.</summary>
    InvalidData,
}

/// <summary>
/// The single failure type that crosses the <see cref="IInventoryApiClient"/> boundary.
/// It means "a complete, trustworthy dataset could not be obtained", and why.
/// </summary>
public sealed class InventoryUnavailableException(
    InventoryFailureReason reason,
    string message,
    Exception? innerException = null,
    TimeSpan? retryAfter = null) : Exception(message, innerException)
{
    public InventoryFailureReason Reason { get; } = reason;

    /// <summary>The server's Retry-After, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
