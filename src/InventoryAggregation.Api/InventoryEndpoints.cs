using System.Globalization;
using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api;

public static class InventoryEndpoints
{
    public const string SummaryRoute = "/api/inventory/summary";

    /// <summary>Suggested wait when the upstream API gave no Retry-After of its own.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>An upstream Retry-After far in the future is passed on as at most one hour.</summary>
    private const int MaxRetryAfterSeconds = 3600;

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
            var (status, title) = Describe(exception.Reason);

            // The crawl has already logged the failure in full, once. This is the per-request line.
            loggerFactory.CreateLogger(typeof(InventoryEndpoints).FullName!).LogWarning(
                "Returning {Status} for the inventory summary: {Reason}: {Message}",
                status, exception.Reason, exception.Message);

            if (status == StatusCodes.Status503ServiceUnavailable)
            {
                var retryAfter = exception.RetryAfter ?? DefaultRetryAfter;
                var seconds = Math.Clamp(Math.Ceiling(retryAfter.TotalSeconds), 0, MaxRetryAfterSeconds);
                httpContext.Response.Headers.RetryAfter = ((int)seconds).ToString(CultureInfo.InvariantCulture);
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
