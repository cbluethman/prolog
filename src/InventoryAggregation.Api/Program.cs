using InventoryAggregation.Api;
using InventoryAggregation.Api.Domain;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Api.Upstream;

var builder = WebApplication.CreateBuilder(args);

// Upstream: options (validated at startup), named HttpClient + RetryHandler, IInventoryApiClient.
builder.Services.AddInventoryApiClient();

// Our own services. All are stateless except the summary service, which owns the cache,
// so singletons are safe: the API client asks IHttpClientFactory for a client per call.
builder.Services.AddSingleton<IInventoryCollector, InventoryCollector>();
builder.Services.AddSingleton<IInventoryAggregator, InventoryAggregator>();
builder.Services.AddSingleton<IInventorySummaryService, InventorySummaryService>();

// Safety net: anything the endpoint does not handle itself still becomes problem+json, not a stack trace.
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapInventoryEndpoints();

app.Run();

/// <summary>Declared so the test project can host the app with WebApplicationFactory.</summary>
public partial class Program;
