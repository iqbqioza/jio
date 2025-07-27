using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jio.Core.Http;

public class HttpClientRetryHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly int _baseDelayMs;
    private readonly int _maxDelayMs;

    public HttpClientRetryHandler(int maxRetries = 3, int baseDelayMs = 1000, int maxDelayMs = 30000)
    {
        _maxRetries = maxRetries;
        _baseDelayMs = baseDelayMs;
        _maxDelayMs = maxDelayMs;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = _baseDelayMs;

        while (true)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode || !ShouldRetry(response) || attempt >= _maxRetries)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                // Network errors should be retried
            }
            catch (TaskCanceledException) when (attempt < _maxRetries && !cancellationToken.IsCancellationRequested)
            {
                // Timeout errors should be retried if not explicitly cancelled
            }

            attempt++;
            
            // Exponential backoff with jitter
            var jitter = Random.Shared.Next(0, 1000);
            var actualDelay = Math.Min(delay + jitter, _maxDelayMs);
            
            await Task.Delay(actualDelay, cancellationToken);
            
            delay = Math.Min(delay * 2, _maxDelayMs);

            // Clone the request for retry
            request = CloneRequest(request);
        }
    }

    private static bool ShouldRetry(HttpResponseMessage response)
    {
        return response.StatusCode >= HttpStatusCode.InternalServerError ||
               response.StatusCode == HttpStatusCode.RequestTimeout ||
               response.StatusCode == HttpStatusCode.TooManyRequests ||
               response.StatusCode == HttpStatusCode.ServiceUnavailable ||
               response.StatusCode == HttpStatusCode.GatewayTimeout;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null)
        {
            // For GET requests, content is usually null
            // For POST/PUT, we'd need to handle content cloning
            // This is simplified for registry operations which are mostly GET
        }

        foreach (var property in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(property.Key), property.Value);
        }

        return clone;
    }
}