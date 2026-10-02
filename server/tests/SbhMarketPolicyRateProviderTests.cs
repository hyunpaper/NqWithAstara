using System.Net;
using Astra.Server.Application;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class SbhMarketPolicyRateProviderTests
{
    [Fact]
    public async Task 표시값과_기준일_갱신시각을_원천미확인으로_보존한다()
    {
        var now = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
        var provider = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(new FixtureHandler(Html("2026-09-26T09:20:09Z"))));

        var snapshot = await provider.GetAsync(now, CancellationToken.None);

        Assert.Equal("available", snapshot.Status);
        Assert.Contains("원천 미확인", snapshot.Source);
        var rate = Assert.Single(snapshot.Rates);
        Assert.Equal("fed", rate.Key);
        Assert.Equal(DateOnly.Parse("2026-09-16"), rate.AsOf);
        Assert.Equal("fresh", rate.DelayStatus);
    }

    [Fact]
    public async Task 오래된_표시값은_지연으로_남기고_점수에_사용하지_않는다()
    {
        var provider = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(new FixtureHandler(Html("2026-09-24T09:20:09Z"))));

        var snapshot = await provider.GetAsync(DateTimeOffset.Parse("2026-09-26T10:00:00Z"), CancellationToken.None);

        Assert.Equal("delayed", snapshot.Status);
        Assert.Equal("stale", Assert.Single(snapshot.Rates).DelayStatus);
        Assert.Contains("점수에 반영하지 않습니다", snapshot.Reason);
    }

    [Fact]
    public async Task 갱신시각이_없으면_표시하지_않는다()
    {
        var provider = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(new FixtureHandler("<script>\\\"policyRates\\\":{\\\"rates\\\":[]}</script>")));

        var snapshot = await provider.GetAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("unavailable", snapshot.Status);
        Assert.Empty(snapshot.Rates);
    }

    [Fact]
    public async Task 십오분_내_성공_응답은_다시_요청하지_않는다()
    {
        var handler = new CountingHandler(Html("2026-09-26T09:20:09Z"));
        var provider = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(handler));
        var now = DateTimeOffset.Parse("2026-09-26T10:00:00Z");

        await provider.GetAsync(now, CancellationToken.None);
        await provider.GetAsync(now.AddMinutes(14), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retry_After_동안에는_재요청하지_않는다()
    {
        var handler = new ThrottledHandler();
        var provider = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(handler));
        var now = DateTimeOffset.Parse("2026-09-26T10:00:00Z");

        var first = await provider.GetAsync(now, CancellationToken.None);
        var second = await provider.GetAsync(now.AddMinutes(1), CancellationToken.None);

        Assert.Equal("unavailable", first.Status);
        Assert.Equal("unavailable", second.Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retry_After는_재기동_뒤에도_저장상태로_재요청을_막는다()
    {
        var now = DateTimeOffset.Parse("2026-09-26T10:00:00Z");
        var store = new MemoryNewsStore();
        var throttled = new ThrottledHandler();
        var first = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(throttled), store);
        await first.GetAsync(now, CancellationToken.None);

        var afterRestartHandler = new CountingHandler(Html("2026-09-26T09:20:09Z"));
        var restarted = new SbhMarketPolicyRateProvider(new NewsOptions(), new HttpClient(afterRestartHandler), store);
        var delayed = await restarted.GetAsync(now.AddMinutes(1), CancellationToken.None);

        Assert.Equal("unavailable", delayed.Status);
        Assert.Equal(0, afterRestartHandler.Calls);
    }

    static string Html(string checkedAt) => $$"""<script>\"policyRates\":{\"rates\":[{\"key\":\"fed\",\"label\":\"미 연준\",\"value\":4,\"prev\":3.75,\"asOf\":\"2026-09-16\",\"note\":\"목표범위\"}],\"checkedAt\":\"{{checkedAt}}\"}</script>""";

    class FixtureHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    sealed class CountingHandler(string body) : FixtureHandler(body)
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    sealed class ThrottledHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        }
    }
}
