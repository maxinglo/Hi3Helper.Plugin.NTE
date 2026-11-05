using System.Net;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Xunit;
using Hi3Helper.Plugin.NTE.Management.Config;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LauncherMetadataTimeoutTests
{
    [Theory]
    [InlineData("1289")]
    [InlineData("3000001")]
    [InlineData("2000013")]
    public async Task LauncherMirrorsComeFromTheSameRegionEndpointsAsGameDownloads(string appId)
    {
        var region = NteConfigProvider.GetRegion(appId);
        Uri primary = new(new Uri(region.ResourceUrls[0]), "/hd/fixture/Version.ini");
        string alternateHost = new Uri(region.ResourceUrls[1]).Host;
        List<string> hosts = [];
        using HttpClient http = new(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            Assert.Equal(primary.AbsolutePath, request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(hosts.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent("fixture") });
        }));
        Assert.Equal("fixture", await new NteLauncherResourceClient(http, region.ZoneHomePageUrl)
            .ReadTextAsync(primary.AbsoluteUri, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { primary.Host, alternateHost }, hosts);
    }

    private const string Primary = "https://yhcdn1.wmupd.com/hd/test/Version.ini";
    private const string Secondary = "https://yhcdn2.wmupd.com/hd/test/Version.ini";
    private const string Referer = "https://yh.wanmei.com/";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointDeadline_FallsBackForStalledHeadersAndBodies(bool bodyStall)
    {
        int cancelled = 0;
        List<string> hosts = [];
        using HttpClient http = new(new Handler(async (request, token) =>
        {
            hosts.Add(request.RequestUri!.Host);
            Assert.Equal(Referer, request.Headers.Referrer!.AbsoluteUri);
            if (request.RequestUri.Host == "yhcdn1.wmupd.com")
            {
                if (bodyStall) return new(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream(() => cancelled++)) };
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancelled++; throw; }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("ready") };
        }));
        NteLauncherResourceClient client = new(http, Referer, TimeSpan.FromMilliseconds(50));
        Assert.Equal("ready", await client.ReadTextAsync(Primary, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "yhcdn1.wmupd.com", "yhcdn2.wmupd.com" }, hosts);
        Assert.Equal(1, cancelled);
        Assert.Equal("ready", await client.ReadTextAsync(Primary.Replace("Version.ini", "AllFiles.xml", StringComparison.Ordinal),
            TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "yhcdn1.wmupd.com", "yhcdn2.wmupd.com", "yhcdn2.wmupd.com" }, hosts);
    }

    [Fact]
    public async Task HttpClientTimeout_IsAnEndpointFailureInsteadOfAUserCancellation()
    {
        int calls = 0;
        using HttpClient http = new(new Handler((_, _) =>
        {
            if (++calls == 1) throw new TaskCanceledException("HTTP request timeout");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fallback") });
        }));
        Assert.Equal("fallback", await new NteLauncherResourceClient(http, Referer).ReadTextAsync(Primary, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UserCancellation_DoesNotStartAnotherEndpoint()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int calls = 0;
        using HttpClient http = new(new Handler(async (_, token) =>
        {
            calls++;
            cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NteLauncherResourceClient(http, Referer,
            TimeSpan.FromMilliseconds(50)).ReadTextAsync(Primary, cancel.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AllEndpointTimeouts_ReturnAnErrorDistinctFromUserCancellation()
    {
        int calls = 0;
        using HttpClient http = new(new Handler(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAsync<TimeoutException>(() => new NteLauncherResourceClient(http, Referer,
            TimeSpan.FromMilliseconds(50)).ReadTextAsync(Primary, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExplicitResourceRoots_DoNotRetryTheSameMirrorTwice()
    {
        int calls = 0;
        using HttpClient http = new(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new NteLauncherResourceClient(http, Referer,
            TimeSpan.FromMilliseconds(50)).ReadTextAsync([Primary, Secondary], TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request, token);
    }

    private sealed class WaitingStream(Action cancelled) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { cancelled(); throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Metadata network bodies must be read asynchronously.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
