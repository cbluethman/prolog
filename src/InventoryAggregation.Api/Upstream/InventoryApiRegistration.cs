using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

public static class InventoryApiRegistration
{
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Registers the options, the named HttpClient with its retry handler, and the API client.</summary>
    public static IServiceCollection AddInventoryApiClient(this IServiceCollection services)
    {
        services.AddOptions<InventoryApiOptions>()
            .BindConfiguration(InventoryApiOptions.SectionName)
            .Validate(
                o => IsUsableBaseUrl(o.BaseUrl),
                "InventoryApi:BaseUrl is missing or is not an https URL (http is accepted for localhost only). Set it with 'dotnet user-secrets' or the InventoryApi__BaseUrl environment variable.")
            .Validate(
                o => IsUsableApiKey(o.ApiKey),
                "InventoryApi:ApiKey is missing, or contains whitespace or control characters (check for a trailing newline). Set it with 'dotnet user-secrets' or the InventoryApi__ApiKey environment variable.")
            .Validate(o => o.PageSize is >= 1 and <= 25, "InventoryApi:PageSize must be between 1 and 25.")
            .Validate(o => o.MaxAttempts is >= 1 and <= 10, "InventoryApi:MaxAttempts must be between 1 and 10.")
            .Validate(o => o.AttemptTimeout > TimeSpan.Zero, "InventoryApi:AttemptTimeout must be positive.")
            .Validate(o => o.CrawlTimeout > TimeSpan.Zero, "InventoryApi:CrawlTimeout must be positive.")
            .Validate(o => o.BaseBackoff >= TimeSpan.Zero, "InventoryApi:BaseBackoff must not be negative.")
            .Validate(o => o.MaxRetryDelay >= TimeSpan.Zero, "InventoryApi:MaxRetryDelay must not be negative.")
            .Validate(o => o.CacheTtl >= TimeSpan.Zero, "InventoryApi:CacheTtl must not be negative.")
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
    /// on Linux, "/some/path" (scheme "file"). Plain http would send the API key in clear text.
    /// </summary>
    private static bool IsUsableBaseUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>A key with a newline in it (easy to get from a file) cannot be sent as a header value.</summary>
    private static bool IsUsableApiKey(string apiKey) =>
        apiKey.Length > 0 && !apiKey.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}
