using System.Net;
using System.Net.Http;
using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Tests for <see cref="GeminiRestApiKeyValidator"/> against a stubbed HTTP transport. Pins the
/// classification rules that turn an HTTP status + error body into a
/// <see cref="GeminiAvailability"/> verdict.
/// </summary>
public class GeminiRestApiKeyValidatorTests
{
    [Fact]
    public async Task ValidateAsync_Http200_Available()
    {
        using var validator = CreateValidator(Respond(200, "{}"));

        Assert.Equal(GeminiAvailability.Available, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Theory]
    [InlineData(400, "{ \"error\": { \"message\": \"API key not valid\", \"status\": \"INVALID_ARGUMENT\" } }")]
    [InlineData(401, "{ \"error\": { \"message\": \"unauthorized\" } }")]
    [InlineData(403, "{ \"error\": { \"message\": \"permission denied\" } }")]
    public async Task ValidateAsync_KeyRejections_InvalidKey(int statusCode, string body)
    {
        using var validator = CreateValidator(Respond(statusCode, body));

        Assert.Equal(GeminiAvailability.InvalidKey, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Fact]
    public async Task ValidateAsync_ApiKeyInvalid_WithDetails_InvalidKey()
    {
        string body = """
            {
              "error": {
                "code": 400,
                "message": "API key not valid. Please pass a valid API key.",
                "status": "INVALID_ARGUMENT",
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.ErrorInfo",
                    "reason": "API_KEY_INVALID",
                    "domain": "googleapis.com" }
                ]
              }
            }
            """;
        using var validator = CreateValidator(Respond(400, body));

        Assert.Equal(GeminiAvailability.InvalidKey, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Fact]
    public async Task ValidateAsync_QuotaReason_NotMisclassifiedAsInvalidKey()
    {
        string body = """
            {
              "error": {
                "code": 429,
                "message": "Quota exceeded.",
                "status": "RESOURCE_EXHAUSTED",
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.ErrorInfo",
                    "reason": "RATE_LIMIT_EXCEEDED",
                    "domain": "googleapis.com" }
                ]
              }
            }
            """;
        using var validator = CreateValidator(Respond(429, body));

        Assert.Equal(GeminiAvailability.QuotaExceeded, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Fact]
    public async Task ValidateAsync_Http429_QuotaExceeded()
    {
        using var validator = CreateValidator(Respond(429, "{}"));

        Assert.Equal(GeminiAvailability.QuotaExceeded, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task ValidateAsync_ServerErrors_NetworkError(int statusCode)
    {
        using var validator = CreateValidator(Respond(statusCode, "{}"));

        Assert.Equal(GeminiAvailability.NetworkError, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Fact]
    public async Task ValidateAsync_TransportFailure_NetworkError()
    {
        using var validator = CreateValidator(_ => throw new HttpRequestException("connection reset"));

        Assert.Equal(GeminiAvailability.NetworkError, await validator.ValidateAsync("AIzaSy" + new string('a', 36)));
    }

    [Fact]
    public async Task ValidateAsync_BlankKey_Missing_WithoutHittingNetwork()
    {
        bool called = false;
        using var validator = CreateValidator(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        Assert.Equal(GeminiAvailability.MissingKey, await validator.ValidateAsync("   "));
        Assert.False(called);
    }

    private static GeminiRestApiKeyValidator CreateValidator(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new StubHandler(handler));
        return new GeminiRestApiKeyValidator(http);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(int statusCode, string body)
    {
        return _ => new HttpResponseMessage((HttpStatusCode)statusCode) { Content = new StringContent(body) };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
