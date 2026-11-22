using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

public static class InventoryApiRegistration
{
    public const string ApiKeyHeader = "X-Api-Key";

    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongestCacheTtl = TimeSpan.FromDays(7);

    /// <summary>Registers the options, the named HttpClient with its retry handler, and the API client.</summary>
    public static IServiceCollection AddInventoryApiClient(this IServiceCollection services)
    {
        services.AddOptions<InventoryApiOptions>()
            .BindConfiguration(InventoryApiOptions.SectionName)
            .Validate(
                o => IsUsableBaseUrl(o.BaseUrl),
                "InventoryApi:BaseUrl is missing or is not a plain https URL (http is accepted for localhost only; no surrounding spaces, query string or fragment). Set it with 'dotnet user-secrets' or the InventoryApi__BaseUrl environment variable.")
            .Validate(
                o => IsUsableApiKey(o.ApiKey),
                "InventoryApi:ApiKey is missing, or contains something other than printable ASCII characters (check for a trailing newline). Set it with 'dotnet user-secrets' or the InventoryApi__ApiKey environment variable.")
            .Validate(o => o.PageSize is >= 1 and <= 25, "InventoryApi:PageSize must be between 1 and 25.")
            .Validate(o => o.MaxAttempts is >= 1 and <= 10, "InventoryApi:MaxAttempts must be between 1 and 10.")
            // Upper bounds too: a duration of weeks overflows the timers and date arithmetic it feeds,
            // which would surface as an error on every request instead of here.
            .Validate(o => o.AttemptTimeout > TimeSpan.Zero && o.AttemptTimeout <= LongestWait, "InventoryApi:AttemptTimeout must be positive and at most 10 minutes.")
            .Validate(o => o.CrawlTimeout > TimeSpan.Zero && o.CrawlTimeout <= LongestWait, "InventoryApi:CrawlTimeout must be positive and at most 10 minutes.")
            .Validate(o => o.BaseBackoff >= TimeSpan.Zero && o.BaseBackoff <= LongestWait, "InventoryApi:BaseBackoff must be between 0 and 10 minutes.")
            .Validate(o => o.MaxRetryDelay >= TimeSpan.Zero && o.MaxRetryDelay <= LongestWait, "InventoryApi:MaxRetryDelay must be between 0 and 10 minutes.")
            .Validate(o => o.CacheTtl >= TimeSpan.Zero && o.CacheTtl <= LongestCacheTtl, "InventoryApi:CacheTtl must be between 0 and 7 days.")
            // Fail at startup with the messages above, not on the first request.
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RetryDelay>(Task.Delay);
        services.AddTransient<RetryHandler>();

        services
            .AddHttpClient(InventoryApiClient.HttpClientName, (serviceProvider, http) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<InventoryApiOptions>>().Value;

                // Trailing slash matters: without it a relative "api/records" would replace the last path segment.
                http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                http.DefaultRequestHeaders.Add(ApiKeyHeader, options.ApiKey);

                // HttpClient.Timeout would span all retries and their waits. The time budget is
                // enforced per attempt by RetryHandler instead, and attempts are bounded.
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            // Do not follow redirects. HttpClient would re-send X-Api-Key to wherever the
            // redirect points, and this API has no reason to redirect. A 3xx is reported as an error.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<RetryHandler>();

        services.AddSingleton<IInventoryApiClient, InventoryApiClient>();

        return services;
    }

    /// <summary>
    /// Uri.TryCreate alone would accept "ftp://x", "localhost:5000" (scheme "localhost") and,
    /// on Linux, "/some/path" (scheme "file"). It also quietly trims spaces that would then
    /// break the address when it is used, so the value must already be trimmed.
    /// Plain http would send the API key in clear text. A query or fragment would be lost
    /// when the request path is appended.
    /// </summary>
    private static bool IsUsableBaseUrl(string? baseUrl) =>
        baseUrl is not null &&
        baseUrl == baseUrl.Trim() &&
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0;

    /// <summary>
    /// Printable ASCII only, which is all an HTTP header value can carry. A key with a newline
    /// (easy to get from a file) or a stray non-ASCII character would pass a looser check and
    /// then fail on every request, looking like an outage.
    /// </summary>
    private static bool IsUsableApiKey(string? apiKey) =>
        apiKey is { Length: > 0 } && apiKey.All(character => character is > ' ' and < (char)0x7F);
}
