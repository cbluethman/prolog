using System.Net;
using System.Text;

namespace InventoryAggregation.Tests.Fakes;

/// <summary>
/// Stands in for the network. Each request consumes the next scripted step, so a test
/// reads as "first a 500, then a 429, then a 200". An unscripted request fails the test.
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();

    public List<Uri?> RequestUris { get; } = [];

    public List<string?> ApiKeysSent { get; } = [];

    public int RequestCount => RequestUris.Count;

    public ScriptedHttpHandler Respond(HttpStatusCode status, string body = "", string? retryAfter = null)
    {
        _steps.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return Task.FromResult(response);
        });
        return this;
    }

    public ScriptedHttpHandler Throw(Exception exception)
    {
        _steps.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    /// <summary>Never answers; ends only when the request is cancelled.</summary>
    public ScriptedHttpHandler Hang()
    {
        _steps.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri);
        ApiKeysSent.Add(request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null);

        return _steps.TryDequeue(out var step)
            ? step(cancellationToken)
            : throw new InvalidOperationException($"Unscripted request number {RequestCount} to {request.RequestUri}.");
    }
}
