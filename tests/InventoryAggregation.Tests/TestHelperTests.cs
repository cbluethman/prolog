using System.Net;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InventoryAggregation.Tests;

/// <summary>
/// The shared helpers in Fakes/TestApp.cs sit under four test classes, so a mistake in one
/// of them would quietly weaken all of those tests.
/// </summary>
public class TestHelperTests
{
    private sealed record Marker(string Name);

    [Fact]
    public void Settings_without_overrides_are_exactly_the_two_required_ones()
    {
        var settings = TestSettings.With();

        Assert.Equal(2, settings.Count);
        Assert.Equal("https://inventory.test", settings["InventoryApi:BaseUrl"]);
        Assert.Equal("test-key", settings["InventoryApi:ApiKey"]);
    }

    [Fact]
    public void Settings_override_replaces_a_required_setting_and_leaves_the_other()
    {
        var settings = TestSettings.With(new Dictionary<string, string?> { ["InventoryApi:ApiKey"] = "another-key" });

        Assert.Equal(2, settings.Count);
        Assert.Equal("another-key", settings["InventoryApi:ApiKey"]);
        Assert.Equal(TestSettings.BaseUrl, settings["InventoryApi:BaseUrl"]);
    }

    [Fact]
    public void Settings_override_can_blank_out_a_required_setting()
    {
        // The startup validation tests depend on a null surviving, not being replaced by the default.
        var settings = TestSettings.With(new Dictionary<string, string?> { ["InventoryApi:BaseUrl"] = null });

        Assert.True(settings.ContainsKey("InventoryApi:BaseUrl"));
        Assert.Null(settings["InventoryApi:BaseUrl"]);
    }

    [Fact]
    public void Settings_override_adds_settings_beside_the_required_ones()
    {
        var settings = TestSettings.With(new Dictionary<string, string?>
        {
            ["InventoryApi:PageSize"] = "2",
            ["InventoryApi:MaxAttempts"] = "3",
        });

        Assert.Equal(4, settings.Count);
        Assert.Equal("2", settings["InventoryApi:PageSize"]);
        Assert.Equal("3", settings["InventoryApi:MaxAttempts"]);
        Assert.Equal(TestSettings.BaseUrl, settings["InventoryApi:BaseUrl"]);
        Assert.Equal(TestSettings.ApiKey, settings["InventoryApi:ApiKey"]);
    }

    [Fact]
    public void Settings_are_a_fresh_dictionary_on_every_call()
    {
        var first = TestSettings.With();
        first["InventoryApi:ApiKey"] = "changed by one test";
        first["InventoryApi:PageSize"] = "2";

        var second = TestSettings.With();

        Assert.NotSame(first, second);
        Assert.Equal(TestSettings.ApiKey, second["InventoryApi:ApiKey"]);
        Assert.False(second.ContainsKey("InventoryApi:PageSize"));
    }

    [Fact]
    public void Settings_do_not_change_the_overrides_they_were_given()
    {
        var overrides = new Dictionary<string, string?> { ["InventoryApi:PageSize"] = "2" };

        TestSettings.With(overrides);

        Assert.Equal("2", Assert.Single(overrides).Value);
    }

    [Fact]
    public async Task Recording_delay_remembers_every_wait_in_order()
    {
        var recorder = new RecordingDelay();
        RetryDelay delay = recorder.Record;

        // Not sorted and not a palindrome, so neither sorting nor reversing would go unnoticed.
        await delay(TimeSpan.FromSeconds(3), CancellationToken.None);
        await delay(TimeSpan.FromSeconds(1), CancellationToken.None);
        await delay(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(
            [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            recorder.Waits);
    }

    [Fact]
    public void Recording_delay_is_already_complete_however_long_the_wait()
    {
        var recorder = new RecordingDelay();

        var waiting = recorder.Record(TimeSpan.FromDays(1), CancellationToken.None);

        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public void Test_app_runs_in_the_testing_environment()
    {
        using var app = TestApp.Create(_ => { });

        Assert.Equal("Testing", app.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }

    [Fact]
    public void Test_app_does_not_read_the_developers_user_secrets()
    {
        using var app = TestApp.Create(_ => { });

        var configuration = Assert.IsAssignableFrom<IConfigurationRoot>(app.Services.GetRequiredService<IConfiguration>());
        var jsonFiles = configuration.Providers.OfType<JsonConfigurationProvider>().Select(json => json.Source.Path);

        Assert.DoesNotContain("secrets.json", jsonFiles);
    }

    [Fact]
    public void Test_app_starts_with_only_the_required_settings()
    {
        using var app = TestApp.Create(_ => { });

        var configuration = app.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(TestSettings.BaseUrl, configuration["InventoryApi:BaseUrl"]);
        Assert.Equal(TestSettings.ApiKey, configuration["InventoryApi:ApiKey"]);
    }

    [Fact]
    public void Test_app_applies_the_settings_it_is_given_over_the_required_ones()
    {
        using var app = TestApp.Create(_ => { }, new Dictionary<string, string?>
        {
            ["InventoryApi:ApiKey"] = "another-key",
            ["InventoryApi:PageSize"] = "2",
        });

        var configuration = app.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("another-key", configuration["InventoryApi:ApiKey"]);
        Assert.Equal("2", configuration["InventoryApi:PageSize"]);
        Assert.Equal(TestSettings.BaseUrl, configuration["InventoryApi:BaseUrl"]);
    }

    [Fact]
    public void Test_app_registers_the_services_the_test_replaces()
    {
        var marker = new Marker("from the test");

        using var app = TestApp.Create(services => services.AddSingleton(marker));

        Assert.Same(marker, app.Services.GetRequiredService<Marker>());
    }

    [Fact]
    public async Task Read_json_returns_the_root_of_the_response_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "title": "Bad Gateway", "status": 502 }"""),
        };

        var json = await response.ReadJsonAsync();

        Assert.Equal("Bad Gateway", json.GetProperty("title").GetString());
        Assert.Equal(502, json.GetProperty("status").GetInt32());
    }
}
