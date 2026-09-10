using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Astra.Server;
using Xunit;

namespace Astra.Server.Tests;

[Collection("TossClient serial")]
public sealed class TossClientTests : IDisposable
{
    readonly string _credentials = Path.GetTempFileName();
    public TossClientTests() { File.WriteAllText(_credentials, "API Key : test-id\nSecret Key : test-secret"); Environment.SetEnvironmentVariable("TOSS_CREDENTIALS_PATH", _credentials); }
    public void Dispose() { Environment.SetEnvironmentVariable("TOSS_CREDENTIALS_PATH", null); File.Delete(_credentials); }

    [Fact]
    public async Task ConcurrentCallsIssueOneOAuthToken()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"token_type\":\"Bearer\",\"expires_in\":3600}")
            : Json("{\"result\":[{\"symbol\":\"AAPL\",\"name\":\"애플\",\"englishName\":\"APPLE INC\",\"currency\":\"USD\",\"status\":\"ACTIVE\"}]}"));
        var client = Client(handler);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.Stocks("AAPL", CancellationToken.None)));
        Assert.Equal(1, handler.Paths.Count(x => x == "/oauth2/token"));
    }

    [Fact]
    public async Task HolidayCalendarUsesNextBusinessDayOpen()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":{\"today\":{\"date\":\"2026-07-03\",\"regularMarket\":null},\"nextBusinessDay\":{\"regularMarket\":{\"startTime\":\"2026-07-06T22:30:00+09:00\",\"endTime\":\"2026-07-07T05:00:00+09:00\"}}}}"));
        var session = await Client(handler).Session(DateTimeOffset.Parse("2026-07-03T23:00:00+09:00"), CancellationToken.None);
        Assert.False(session.IsOpen); Assert.Equal("휴장", session.Label); Assert.Equal(DateTimeOffset.Parse("2026-07-06T22:30:00+09:00"), session.NextOpen);
    }

    [Fact]
    public async Task ForbiddenTokenReturnsSanitizedIpGuidance()
    {
        var handler = new FixtureHandler((request, count) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<TossAuthException>(() => Client(handler).GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("허용 IP", error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.Equal("https://developers.tossinvest.com/docs", error.GuideUrl);
    }

    static TossClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://openapi.tossinvest.com/") });
    static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    sealed class FixtureHandler(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        int _count; public ConcurrentBag<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Paths.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(reply(request, Interlocked.Increment(ref _count))); }
    }
}

[CollectionDefinition("TossClient serial", DisableParallelization = true)] public sealed class TossClientCollection;
