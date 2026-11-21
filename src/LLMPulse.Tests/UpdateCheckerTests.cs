using System.Net;
using LLMPulse.Core;
using Xunit;

namespace LLMPulse.Tests;

public class UpdateCheckerTests
{
    private sealed class Stub(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    [Theory]
    [InlineData("v0.6.0", "0.5.0", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("v0.4.9", "0.5.0", false)]
    [InlineData("v1.0.0-beta.1", "0.5.0", true)]
    [InlineData("nightly", "0.5.0", false)]
    public void IsNewer(string tag, string current, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsNewer(tag, Version.Parse(current)));

    [Fact]
    public async Task Check_ParsesRelease()
    {
        var c = new UpdateChecker(new HttpClient(new Stub(HttpStatusCode.OK, """{"tag_name":"v0.6.0","html_url":"https://example/r"}""")));
        var info = await c.CheckAsync(new Version(0, 5, 0));
        Assert.NotNull(info);
        Assert.True(info!.IsNewer);
        Assert.Equal("https://example/r", info.Url);
    }

    [Fact]
    public async Task Check_NoRelease_ReturnsNull()
    {
        var c = new UpdateChecker(new HttpClient(new Stub(HttpStatusCode.NotFound, "{}")));
        Assert.Null(await c.CheckAsync(new Version(0, 5, 0)));
    }
}