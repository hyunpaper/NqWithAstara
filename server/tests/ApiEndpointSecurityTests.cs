using Astra.Server.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ApiEndpointSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1", "localhost", "http://localhost:5188", true)]
    [InlineData("127.0.0.1", "localhost", "https://example.com", false)]
    [InlineData("192.168.1.20", "localhost", "", false)]
    [InlineData("127.0.0.1", "example.com", "", false)]
    public void 뉴스_정리_API는_로컬_요청만_허용한다(string remoteAddress, string requestHost, string origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteAddress);
        context.Request.Host = new HostString(requestHost);
        if (origin.Length > 0) context.Request.Headers.Origin = origin;

        Assert.Equal(expected, ApiEndpoints.IsLoopback(context));
    }
}
