using System.Text.Json;
using InventoryAggregation.Api.Upstream;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryAggregation.Tests.Fakes;

/// <summary>The settings every test host needs before the application will start.</summary>
public static class TestSettings
{
    public const string BaseUrl = "https://inventory.test";
    public const string ApiKey = "test-key";

    /// <summary>The two required settings, plus whatever the test adds or overrides.</summary>
    public static Dictionary<string, string?> With(IEnumerable<KeyValuePair<string, string?>>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["InventoryApi:BaseUrl"] = BaseUrl,
            ["InventoryApi:ApiKey"] = ApiKey,
        };

        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }
}

/// <summary>Stands in for the wait between retries: remembers each wait and returns at once.</summary>
public sealed class RecordingDelay
{
    public List<TimeSpan> Waits { get; } = [];

    /// <summary>Has the shape of <see cref="RetryDelay"/>, so it can be passed wherever one is needed.</summary>
    public Task Record(TimeSpan wait, CancellationToken cancellationToken)
    {
        Waits.Add(wait);
        return Task.CompletedTask;
    }
}

public static class TestApp
{
    /// <summary>
    /// The whole application in memory. <paramref name="replaceServices"/> swaps out whatever
    /// the test wants faked; everything else is the real wiring from Program.cs.
    /// </summary>
    public static WebApplicationFactory<Program> Create(
        Action<IServiceCollection> replaceServices,
        IEnumerable<KeyValuePair<string, string?>>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            // Not Development: that would load the developer's real user secrets.
            host.UseEnvironment("Testing");

            foreach (var (key, value) in TestSettings.With(settings))
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(replaceServices);
        });

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
