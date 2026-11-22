using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api;

public static class InventoryEndpoints
{
    public const string SummaryRoute = "/api/inventory/summary";

    /// <summary>Suggested wait when the upstream API gave no Retry-After of its own.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet(SummaryRoute, GetSummaryAsync);

    private static async Task<IResult> GetSummaryAsync(
        IInventorySummaryService summaryService,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await summaryService.GetSummaryAsync(cancellationToken));
        }
        catch (InventoryUnavailableException exception)
        {
            loggerFactory.CreateLogger(typeof(InventoryEndpoints).FullName!).LogError(
                exception, "Could not build the inventory summary: {Reason}", exception.Reason);

            var (status, title) = Describe(exception.Reason);

            if (status == StatusCodes.Status503ServiceUnavailable)
            {
                var retryAfter = exception.RetryAfter ?? DefaultRetryAfter;
                httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            }

            // RFC 7807 problem+json. The detail is one of our own messages, never a stack trace.
            return Results.Problem(statusCode: status, title: title, detail: exception.Message);
        }
    }

    /// <summary>
    /// 503: we could not get an answer right now and trying later may help.
    /// 504: the upstream API was too slow.
    /// 502: the upstream API answered, but with something we cannot use.
    /// </summary>
    private static (int Status, string Title) Describe(InventoryFailureReason reason) => reason switch
    {
        InventoryFailureReason.Unreachable =>
            (StatusCodes.Status503ServiceUnavailable, "The Inventory API is unreachable."),
        InventoryFailureReason.RateLimited =>
            (StatusCodes.Status503ServiceUnavailable, "The Inventory API is rate limiting this service."),
        InventoryFailureReason.Timeout =>
            (StatusCodes.Status504GatewayTimeout, "The Inventory API timed out."),
        InventoryFailureReason.Unauthorized =>
            (StatusCodes.Status502BadGateway, "The Inventory API rejected this service's credentials."),
        InventoryFailureReason.InvalidData =>
            (StatusCodes.Status502BadGateway, "The Inventory API returned incomplete or inconsistent data."),
        _ =>
            (StatusCodes.Status502BadGateway, "The Inventory API returned an error."),
    };
}
