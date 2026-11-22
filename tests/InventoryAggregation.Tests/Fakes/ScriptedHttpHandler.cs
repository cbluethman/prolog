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

    /// <summary>Every response handed out, in order, so a test can check they were released.</summary>
    public List<TrackedResponse> Responses { get; } = [];

    public ScriptedHttpHandler Respond(
        HttpStatusCode status, string body = "", string? retryAfter = null, string? location = null, string? contentType = null)
    {
        _steps.Enqueue(_ =>
        {
            var response = new TrackedResponse(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            Responses.Add(response);

            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            if (location is not null)
            {
                response.Headers.Location = new Uri(location);
            }

            if (contentType is not null)
            {
                // Set raw, so a test can send a Content-Type the framework itself would refuse to build.
                response.Content.Headers.Remove("Content-Type");
                response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            return Task.FromResult<HttpResponseMessage>(response);
        });
        return this;
    }

    /// <summary>Headers arrive, then the connection drops while the body is being read.</summary>
    public ScriptedHttpHandler RespondWithBrokenBody(HttpStatusCode status)
    {
        _steps.Enqueue(_ =>
        {
            var response = new TrackedResponse(status)
            {
                Content = new ScriptedBody(_ => Task.FromException(new IOException("connection reset mid-body"))),
            };
            Responses.Add(response);
            return Task.FromResult<HttpResponseMessage>(response);
        });
        return this;
    }

    /// <summary>Headers arrive, then the body never does; reading ends only when cancelled.</summary>
    public ScriptedHttpHandler RespondWithStalledBody(HttpStatusCode status)
    {
        _steps.Enqueue(_ =>
        {
            var response = new TrackedResponse(status)
            {
                Content = new ScriptedBody(cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)),
            };
            Responses.Add(response);
            return Task.FromResult<HttpResponseMessage>(response);
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

    public sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ScriptedBody(Func<CancellationToken, Task> read) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            read(CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            read(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
