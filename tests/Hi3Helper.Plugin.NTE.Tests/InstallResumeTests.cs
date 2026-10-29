using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class InstallResumeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteInstallResumeTests", Guid.NewGuid().ToString("N"));
    public InstallResumeTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PublicInstallApi_ReportsOnlyRemainingBytesAndResumesWithoutRedownloadingCompletedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "one.dat"), "DONE", TestContext.Current.CancellationToken);
        string temporary = Path.Combine(root, "two.dat.tmp");
        NteGameInstaller.PrepareTemporaryFile(temporary, Handler.Hash("MORE!"), 5);
        await File.WriteAllTextAsync(temporary, "MO", TestContext.Current.CancellationToken);
        using Handler handler = new(); using HttpClient http = new(handler);
        NteGameManager manager = new(http); manager.SetGamePath(root);
        NteGameInstaller installer = new(manager, http);
        try
        {
            Guid token = Guid.NewGuid();
            installer.GetGameSizeAsync(GameInstallerKind.Install, in token, out nint sizeResult);
            long total = await sizeResult.AsTask<long>();
            installer.GetGameDownloadedSizeAsync(GameInstallerKind.Install, in token, out nint downloadedResult);
            long downloaded = await downloadedResult.AsTask<long>();
            Assert.Equal(9, total); Assert.Equal(6, downloaded); Assert.Equal(3, total - downloaded);
            List<InstallProgress> progress = [];
            installer.StartInstallAsync((in InstallProgress value) => progress.Add(value), null, in token, out nint install);
            await install.AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(1, handler.Downloads);
            Assert.Equal(2, handler.RangeStart);
            Assert.Equal("MORE!", await File.ReadAllTextAsync(Path.Combine(root, "two.dat"), TestContext.Current.CancellationToken));
            Assert.Equal("DONE", await File.ReadAllTextAsync(Path.Combine(root, "one.dat"), TestContext.Current.CancellationToken));
            Assert.Equal(9, progress[^1].DownloadedBytes); Assert.Equal(2, progress[^1].DownloadedCount);
            Assert.Contains(progress, value => value.DownloadedBytes == 6);
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Downloads;
        internal long? RangeStart;
        internal static string Hash(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            HttpStatusCode status = HttpStatusCode.OK;
            if (path.EndsWith("Version.ini", StringComparison.Ordinal)) content = new StringContent("FileListURL=https://launcher.test/1/AllFiles.xml");
            else if (path.EndsWith("AllFiles.xml", StringComparison.Ordinal)) content = new StringContent("<All_Files><File Path=\"/ResFilesM/1289/PatcherConfig/PatcherConfig.json\"/></All_Files>");
            else if (path.EndsWith("PatcherConfig.json.zip", StringComparison.Ordinal))
                content = Zip("PatcherConfig.json", """{"branchName":"PC_140","appId":"1289","gameResUrl":["https://resources.test/clientRes"]}""");
            else if (path.EndsWith("config.xml", StringComparison.Ordinal)) content = new StringContent("<config><ResVersion>1.4.17</ResVersion></config>");
            else if (path.EndsWith("ResList.bin.zip", StringComparison.Ordinal))
                content = Zip("ResList.bin", $"<ResList version=\"1.4.17\"><Res filename=\"one.dat\" filesize=\"4\" md5=\"{Hash("DONE")}\"/><Res filename=\"two.dat\" filesize=\"5\" md5=\"{Hash("MORE!")}\"/></ResList>");
            else
            {
                Assert.Contains(Hash("MORE!"), path);
                Interlocked.Increment(ref Downloads);
                RangeStart = request.Headers.Range?.Ranges.Single().From;
                Assert.Equal(2, RangeStart);
                content = new ByteArrayContent("RE!"u8.ToArray());
                content.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, 5);
                status = HttpStatusCode.PartialContent;
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
        private static ByteArrayContent Zip(string name, string text)
        {
            using MemoryStream bytes = new();
            using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, true))
            using (StreamWriter writer = new(zip.CreateEntry(name).Open(), Encoding.UTF8)) writer.Write(text);
            return new ByteArrayContent(bytes.ToArray());
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}
