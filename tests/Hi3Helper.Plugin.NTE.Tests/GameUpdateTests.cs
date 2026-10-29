using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GameUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NteUpdateTests", Guid.NewGuid().ToString("N"));

    public GameUpdateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void InstalledVersion_UsesOfficialRecordInsteadOfStaleCollapseVersion()
    {
        string path = Path.Combine(_directory, "NTELauncher/UserData/Patcher/PatcherSDK/config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<config><ResVersion>1.4.9</ResVersion></config>");
        File.WriteAllText(Path.Combine(_directory, ".collapse_nte_config"), "GameVersion=1.0.22");
        NteGameManager manager = new();
        manager.SetGamePath(_directory);
        manager.LoadConfig();
        manager.GetCurrentGameVersion(out GameVersion actual);
        Assert.Equal(new GameVersion("1.4.9"), actual);
    }

    [Fact]
    public void ActiveBranch_ComesFromOfficialPatcherConfig()
    {
        NtePatcherConfig config = NtePatcherConfig.Parse("""{"branchName":"PC_140","appId":"1289","gameResUrl":["https://yhcdn1.wmupd.com/clientRes"]}""");
        Assert.Equal("PC_140", config.BranchName);
        Assert.Throws<InvalidDataException>(() => NtePatcherConfig.Parse("""{"branchName":"../publish_PC","appId":"1289","gameResUrl":[]}"""));
    }

    [Fact]
    public async Task CorruptDownload_PreservesInstalledFile()
    {
        string output = Path.Combine(_directory, "game.pak");
        string temporary = output + ".tmp";
        await File.WriteAllTextAsync(output, "original", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(temporary, "wrong", TestContext.Current.CancellationToken);
        string expected = Convert.ToHexString(MD5.HashData("right"u8));
        await Assert.ThrowsAsync<InvalidDataException>(() => NteGameInstaller.CompleteDownloadAsync(
            temporary, output, 5, expected, TestContext.Current.CancellationToken));
        Assert.Equal("original", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task ValidDownload_ReplacesInstalledFileAfterVerification()
    {
        string output = Path.Combine(_directory, "game.pak");
        await File.WriteAllTextAsync(output, "original", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output + ".tmp", "right", TestContext.Current.CancellationToken);
        await NteGameInstaller.CompleteDownloadAsync(output + ".tmp", output, 5,
            Convert.ToHexString(MD5.HashData("right"u8)), TestContext.Current.CancellationToken);
        Assert.Equal("right", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RangeDownload_RejectsFullArchiveAndWrongOffsets()
    {
        using HttpResponseMessage full = new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) };
        Assert.Throws<InvalidDataException>(() => NteGameInstaller.ValidateRangeResponse(full, 20, 29));
        using HttpResponseMessage partial = new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[10]) };
        partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 9, 100);
        Assert.Throws<InvalidDataException>(() => NteGameInstaller.ValidateRangeResponse(partial, 20, 29));
        partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(20, 29, 100);
        NteGameInstaller.ValidateRangeResponse(partial, 20, 29);
    }

    [Theory]
    [InlineData("../outside.pak")]
    [InlineData("C:/outside.pak")]
    public void ResourcePath_CannotEscapeGameDirectory(string path)
        => Assert.Throws<InvalidDataException>(() => NteGameInstaller.ResolveResourcePath(_directory, path));

    [Fact]
    public void UpdatePlan_UnchangedManifestEntryDoesNotReadFileContents()
    {
        string file = Path.Combine(_directory, "unchanged.pak");
        File.WriteAllText(file, "unchanged");
        NteResListParser manifest = NteResListParser.Parse("""<ResList version="1.4.17"><Res filename="unchanged.pak" filesize="9" md5="643c53e4f0a2622f62e141be20f8c31e"/></ResList>""");
        using FileStream exclusive = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int checkedCount = 0;
        var pending = NteGameInstaller.BuildDownloadTasks(manifest, _directory, true, true,
            TestContext.Current.CancellationToken, manifest,
            (in InstallProgress progress) => checkedCount = progress.DownloadedCount);
        Assert.Empty(pending);
        Assert.Equal(1, checkedCount);
        Assert.Single(NteGameInstaller.BuildDownloadTasks(manifest, _directory, true, true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UpdatePlan_ChangedHashAtSameSizeIsNotSkipped()
    {
        File.WriteAllText(Path.Combine(_directory, "changed.pak"), "wrong");
        NteResListParser previous = NteResListParser.Parse("""<ResList version="1.4.9"><Res filename="changed.pak" filesize="5" md5="old"/></ResList>""");
        string expected = Convert.ToHexString(MD5.HashData("right"u8));
        NteResListParser target = NteResListParser.Parse($"<ResList version=\"1.4.17\"><Res filename=\"changed.pak\" filesize=\"5\" md5=\"{expected}\"/></ResList>");
        Assert.Single(NteGameInstaller.BuildDownloadTasks(target, _directory, true, true,
            TestContext.Current.CancellationToken, previous));
    }

    [Fact]
    public void Resume_DiscardsPartialDataFromAnotherResourceVersion()
    {
        string path = Path.Combine(_directory, "game.pak.tmp");
        NteGameInstaller.PrepareTemporaryFile(path, "old-hash", 5);
        File.WriteAllText(path, "old");
        NteGameInstaller.PrepareTemporaryFile(path, "new-hash", 5);
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "new");
        NteGameInstaller.PrepareTemporaryFile(path, "new-hash", 5);
        Assert.Equal("new", File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
