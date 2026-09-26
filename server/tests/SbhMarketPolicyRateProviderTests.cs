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

    static string Html(string checkedAt) => $$"""<script>\"policyRates\":{\"rates\":[{\"key\":\"fed\",\"label\":\"미 연준\",\"value\":4,\"prev\":3.75,\"asOf\":\"2026-09-16\",\"note\":\"목표범위\"}],\"checkedAt\":\"{{checkedAt}}\"}</script>""";

    sealed class FixtureHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
