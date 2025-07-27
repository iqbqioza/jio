using System.Net;
using FluentAssertions;
using Jio.Core.Http;
using Moq;
using Moq.Protected;

namespace Jio.Core.Tests.Http;

public sealed class HttpClientRetryHandlerTests
{
    private readonly Mock<HttpMessageHandler> _innerHandlerMock;
    private readonly HttpClientRetryHandler _retryHandler;
    private readonly HttpClient _httpClient;

    public HttpClientRetryHandlerTests()
    {
        _innerHandlerMock = new Mock<HttpMessageHandler>();
        _retryHandler = new HttpClientRetryHandler(maxRetries: 3, baseDelayMs: 10, maxDelayMs: 1000);
        _retryHandler.InnerHandler = _innerHandlerMock.Object;
        _httpClient = new HttpClient(_retryHandler);
    }

    [Fact]
    public async Task SendAsync_WithSuccessfulResponse_ReturnsResponseWithoutRetry()
    {
        var expectedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("success")
        };

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(expectedResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("success");

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithServerError_RetriesBeforeReturning()
    {
        var serverErrorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("success after retry")
        };

        _innerHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(serverErrorResponse)
            .ReturnsAsync(successResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("success after retry");

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithTooManyRequests_RetriesWithBackoff()
    {
        var tooManyRequestsResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(tooManyRequestsResponse)
            .ReturnsAsync(successResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithMaxRetriesExceeded_ReturnsLastFailureResponse()
    {
        var serverErrorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(serverErrorResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // Should attempt initial call + 3 retries = 4 total calls
        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(4),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithNetworkException_RetriesAndEventuallySucceeds()
    {
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("success")
        };

        _innerHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"))
            .ThrowsAsync(new HttpRequestException("Network error"))
            .ReturnsAsync(successResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(3),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithTimeoutException_RetriesIfNotCancelled()
    {
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Timeout"))
            .ReturnsAsync(successResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithCancellationRequested_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());

        var act = async () => await _httpClient.GetAsync("https://example.com", cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public async Task SendAsync_WithVariousStatusCodes_RetriesOnlyRetryableErrors(HttpStatusCode statusCode, bool shouldRetry)
    {
        var errorResponse = new HttpResponseMessage(statusCode);
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(errorResponse)
            .ReturnsAsync(successResponse);

        var response = await _httpClient.GetAsync("https://example.com");

        if (shouldRetry)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            _innerHandlerMock
                .Protected()
                .Verify(
                    "SendAsync",
                    Times.Exactly(2),
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>());
        }
        else
        {
            response.StatusCode.Should().Be(statusCode);
            _innerHandlerMock
                .Protected()
                .Verify(
                    "SendAsync",
                    Times.Once(),
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>());
        }
    }

    [Fact]
    public async Task SendAsync_WithExponentialBackoff_IncreaseDelayBetweenRetries()
    {
        var retryHandler = new HttpClientRetryHandler(maxRetries: 2, baseDelayMs: 100, maxDelayMs: 1000);
        retryHandler.InnerHandler = _innerHandlerMock.Object;
        var httpClient = new HttpClient(retryHandler);

        var serverErrorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(serverErrorResponse);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await httpClient.GetAsync("https://example.com");
        stopwatch.Stop();

        // Should take at least some time due to delays (base + exponential + jitter)
        // Even with jitter, it should take at least the base delay times number of retries
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(100);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(3), // Initial + 2 retries
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ClonesRequestForRetries()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var serverErrorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK);

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => capturedRequests.Add(req))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
                capturedRequests.Count == 1 ? serverErrorResponse : successResponse);

        var originalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        originalRequest.Headers.Add("X-Custom-Header", "test-value");

        var response = await _httpClient.SendAsync(originalRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedRequests.Should().HaveCount(2);

        // Both requests should have the same URI and headers but be different instances
        capturedRequests[0].RequestUri.Should().Be(capturedRequests[1].RequestUri);
        capturedRequests[0].Headers.GetValues("X-Custom-Header").Should().BeEquivalentTo(
            capturedRequests[1].Headers.GetValues("X-Custom-Header"));
        capturedRequests[0].Should().NotBeSameAs(capturedRequests[1]);
    }

    [Fact]
    public async Task SendAsync_WithCustomRetryConfiguration_RespectsConfiguration()
    {
        var customRetryHandler = new HttpClientRetryHandler(maxRetries: 1, baseDelayMs: 50, maxDelayMs: 200);
        customRetryHandler.InnerHandler = _innerHandlerMock.Object;
        var customHttpClient = new HttpClient(customRetryHandler);

        var serverErrorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(serverErrorResponse);

        var response = await customHttpClient.GetAsync("https://example.com");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // Should attempt initial call + 1 retry = 2 total calls
        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithMaxDelayReached_CapsDelayAtMaxValue()
    {
        var retryHandler = new HttpClientRetryHandler(maxRetries: 5, baseDelayMs: 100, maxDelayMs: 150);
        retryHandler.InnerHandler = _innerHandlerMock.Object;
        var httpClient = new HttpClient(retryHandler);

        var serverErrorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        _innerHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(serverErrorResponse);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await httpClient.GetAsync("https://example.com");
        stopwatch.Stop();

        // Even with 5 retries, the max delay should cap the total time
        // The delay should not exponentially grow beyond maxDelayMs
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(2000); // Reasonable upper bound

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        _innerHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(6), // Initial + 5 retries
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }
}