using System.Net;
using Astra.Server.Application;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class PapagoNewsTranslatorTests
{
    [Fact]
    public async Task PapagoTranslatesTitleAndSourceWithConfiguredHeaders()
    {
        var handler = new Handler();
        var translator = new PapagoNewsTranslator(new NewsOptions { PapagoClientId = "id", PapagoClientSecret = "secret" }, new HttpClient(handler));
        var result = await translator.TranslateAsync("Market rally", "Yahoo Finance", CancellationToken.None);
        Assert.Equal(("번역 제목", "번역 출처"), result);
        Assert.Equal("id", handler.Headers["X-NCP-APIGW-API-KEY-ID"]);
        Assert.Equal("secret", handler.Headers["X-NCP-APIGW-API-KEY"]);
        Assert.All(handler.Bodies, body => Assert.Contains("target=ko", body));
        Assert.DoesNotContain("secret", string.Join("|", handler.Bodies));
    }

    [Fact]
    public async Task MissingCredentialsDoesNotCallProvider()
    {
        var handler = new Handler();
        var result = await new PapagoNewsTranslator(new NewsOptions(), new HttpClient(handler))
            .TranslateAsync("제목", "출처", CancellationToken.None);
        Assert.Null(result);
        Assert.Empty(handler.Bodies);
    }



    [Fact]
    public async Task HttpErrorInvalidJsonAndTimeoutFallBackToNullWithoutThrowing()
    {
        foreach (var mode in new[] { FailureMode.Http, FailureMode.Json, FailureMode.Timeout })
        {
            var result = await new PapagoNewsTranslator(
                new NewsOptions { PapagoClientId = "id", PapagoClientSecret = "secret" },
                new HttpClient(new FailureHandler(mode)))
                .TranslateAsync("제목", "출처", CancellationToken.None);
            Assert.Null(result);
        }
    }
    enum FailureMode { Http, Json, Timeout }

    sealed class FailureHandler(FailureMode mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => mode switch
            {
                FailureMode.Http => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
                FailureMode.Json => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") }),
                _ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"))
            };
    }

    sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new();
        public List<string> Bodies { get; } = [];
        int _count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers) Headers[header.Key] = string.Join(",", header.Value);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            _count++;
            var text = _count == 1 ? "번역 제목" : "번역 출처";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"message\":{{\"result\":{{\"translatedText\":\"{text}\"}}}}}}")
            };
        }
    }
}
