namespace InventoryAggregation.Api;

/// <summary>
/// Settings bound from the "InventoryApi" configuration section.
/// <see cref="BaseUrl"/> and <see cref="ApiKey"/> must come from user secrets or
/// environment variables; they are never stored in appsettings.json.
/// </summary>
public sealed class InventoryApiOptions
{
    public const string SectionName = "InventoryApi";

    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>Records requested per page. The upstream API accepts 1 to 25.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>Total tries per page request, including the first one.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a single try may take before it is abandoned and retried.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>First backoff delay when the server gives no Retry-After; doubles on each retry.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on any single wait between tries, including a server-supplied Retry-After.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time budget for one whole crawl of every page, including all retries and waits.</summary>
    public TimeSpan CrawlTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a successfully built summary is reused before the upstream API is crawled again.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
