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
                o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _),
                "InventoryApi:BaseUrl is missing or is not an absolute URL. Set it with 'dotnet user-secrets' or the InventoryApi__BaseUrl environment variable.")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ApiKey),
                "InventoryApi:ApiKey is missing. Set it with 'dotnet user-secrets' or the InventoryApi__ApiKey environment variable.")
            .Validate(o => o.PageSize is >= 1 and <= 25, "InventoryApi:PageSize must be between 1 and 25.")
            .Validate(o => o.MaxAttempts >= 1, "InventoryApi:MaxAttempts must be at least 1.")
            .Validate(o => o.AttemptTimeout > TimeSpan.Zero, "InventoryApi:AttemptTimeout must be positive.")
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
            .AddHttpMessageHandler<RetryHandler>();

        services.AddSingleton<IInventoryApiClient, InventoryApiClient>();

        return services;
    }
}
