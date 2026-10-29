using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class OfficialPatcherStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NteOfficialStateTests", Guid.NewGuid().ToString("N"));
    private string Sdk => Path.Combine(_root, "NTELauncher/UserData/Patcher/PatcherSDK");
    public OfficialPatcherStateTests() => Directory.CreateDirectory(Sdk);

    [Fact]
    public void RemoteConfiguration_DecodesKnownOfficialSampleAndRoundTrips()
    {
        string[] sample = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures/official-remote-content.txt"));
        string xml = NteOfficialPatcherState.DecodeRemoteConfiguration(sample[1]);
        Assert.Equal(sample[0], Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))));
        Assert.Equal("1.4.17", NteOfficialPatcherState.ParseXml(xml).SelectSingleNode("config/ResVersion")!.InnerText);
        Assert.Equal(xml, NteOfficialPatcherState.DecodeRemoteConfiguration(Convert.ToBase64String(NteOfficialPatcherState.EncodeRemoteConfiguration(xml))));
    }

    [Fact]
    public void Update_PreservesInstalledBaseVersionInsteadOfDownloadingAnotherOptionalBranch()
    {
        File.WriteAllText(Path.Combine(Sdk, "ResList.xml"), """<ResList version="1.4.9"><BaseVersion version="0.101.9"><ResList><Res filename="old-base.pak" filesize="5" md5="old" /></ResList></BaseVersion></ResList>""");
        byte[] remote = Encoding.UTF8.GetBytes("""<ResList version="1.4.17"><Res filename="current.pak" filesize="4" md5="main"/><BaseVersion version="0.101.17"><ResList><Res filename="uninstalled-base.pak" filesize="9" md5="new"/></ResList></BaseVersion></ResList>""");
        NteResListParser selected = NteResListParser.Parse(NteOfficialPatcherState.SelectInstalledBaseVersions(remote, _root));
        Assert.Equal("current.pak", Assert.Single(selected.Resources).Filename);
        Assert.Equal("old-base.pak", Assert.Single(selected.BaseVersionResources).Filename);
        Assert.Empty(NteResListParser.Parse(NteOfficialPatcherState.SelectInstalledBaseVersions(remote, null)).BaseVersionResources);
    }

    [Fact]
    public void LocalManifest_FlattensArchiveEntriesAndRecordsActualFileTimestamps()
    {
        string payload = Path.Combine(_root, "game.pak");
        File.WriteAllText(payload, "right");
        File.SetLastWriteTimeUtc(payload, DateTime.UnixEpoch.AddSeconds(1234567890));
        byte[] manifest = Encoding.UTF8.GetBytes("""<ResList version="1.4.17"><Package><Pak><Entry name="game.pak" size="5" md5="hash" check="1" offset="10"/></Pak></Package></ResList>""");
        byte[] encoded = NteOfficialPatcherState.CreateLocalManifest(manifest, "1289", _root);
        var decoded = NteOfficialPatcherState.ParseXml(Encoding.UTF8.GetString(NteResListDecryptor.Decrypt(encoded)));
        Assert.Null(decoded.SelectSingleNode("ResList/Package"));
        var file = decoded.SelectSingleNode("ResList/Res")!;
        Assert.Equal("game.pak", file.Attributes!["filename"]!.Value);
        Assert.Equal("5", file.Attributes["filesize"]!.Value);
        Assert.Equal("1234567890", file.Attributes["timestamp"]!.Value);
        Assert.Null(file.Attributes["offset"]);
        Assert.Null(file.Attributes["check"]);
    }

    [Fact]
    public void Configuration_MatchesObservedOfficialFieldsAndKeepsLocalPreferences()
    {
        string old = """<config><LocalBranch>PC_140</LocalBranch><ResVersion>1.4.9</ResVersion><ResCount>1</ResCount><ResSize>4</ResSize><Hash>old-hash</Hash><BaseVerson><item>keep</item></BaseVerson><Extra><speed>50</speed><listHash>old</listHash></Extra></config>""";
        string release = """<config><ResVersion>1.4.17</ResVersion><ResSize>9</ResSize><Hash>remote-hash</Hash><Extra><listHash>list</listHash><diffHash>diff</diffHash></Extra></config>""";
        byte[] manifest = "<ResList version=\"1.4.17\"><Res/><Package><Pak><Entry/></Pak></Package></ResList>"u8.ToArray();
        var updated = NteOfficialPatcherState.ParseXml(NteOfficialPatcherState.CreateConfiguration(old, release, "PC_140", manifest));
        Assert.Equal("1.4.17", updated.SelectSingleNode("config/ResVersion")!.InnerText);
        Assert.Equal("2", updated.SelectSingleNode("config/ResCount")!.InnerText);
        Assert.Equal("old-hash", updated.SelectSingleNode("config/Hash")!.InnerText);
        Assert.Equal("keep", updated.SelectSingleNode("config/BaseVerson/item")!.InnerText);
        Assert.Equal("50", updated.SelectSingleNode("config/Extra/speed")!.InnerText);
        Assert.Equal("list", updated.SelectSingleNode("config/Extra/listHash")!.InnerText);
        Assert.Equal(release, NteOfficialPatcherState.DecodeRemoteConfiguration(updated.SelectSingleNode("config/RemoteContent")!.InnerText));
        Assert.Throws<InvalidDataException>(() => NteOfficialPatcherState.CreateConfiguration(old, release, "wrong-branch", manifest));
    }

    [Fact]
    public void Commit_RejectsMismatchedManifestBeforeChangingEitherSdkFile()
    {
        string config = Path.Combine(Sdk, "config.xml"), manifest = Path.Combine(Sdk, "ResList.xml");
        File.WriteAllText(config, "original config");
        File.WriteAllText(manifest, "original manifest");
        Assert.Throws<InvalidDataException>(() => NteOfficialPatcherState.Commit(_root, "PC_140", "1289",
            "<config><Extra><listHash>wrong</listHash></Extra></config>", "manifest"u8.ToArray(), "manifest"u8.ToArray(), TestContext.Current.CancellationToken));
        Assert.Equal("original config", File.ReadAllText(config));
        Assert.Equal("original manifest", File.ReadAllText(manifest));
    }

    [Fact]
    public void Commit_WritesConsistentOfficialStateAndPreservesOriginalBackups()
    {
        string old = """<config><LocalBranch>PC_140</LocalBranch><ResVersion>1.4.9</ResVersion><Extra/></config>""";
        File.WriteAllText(Path.Combine(Sdk, "config.xml"), old, new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(Sdk, "ResList.xml"), "old manifest");
        File.WriteAllText(Path.Combine(_root, "game.pak"), "right");
        byte[] selected = "<ResList version=\"1.4.17\"><Res filename=\"game.pak\" filesize=\"5\" md5=\"hash\"/></ResList>"u8.ToArray();
        string hash = Convert.ToHexString(MD5.HashData(selected));
        string release = $"<config><ResVersion>1.4.17</ResVersion><ResSize>5</ResSize><Extra><listHash>{hash}</listHash><diffHash>diff</diffHash></Extra></config>";
        string backups = Path.Combine(_root, "backups");
        NteOfficialPatcherState.Commit(_root, "PC_140", "1289", release, selected, selected, TestContext.Current.CancellationToken, backups);
        var config = NteOfficialPatcherState.ParseXml(File.ReadAllText(Path.Combine(Sdk, "config.xml")));
        Assert.Equal("1.4.17", config.SelectSingleNode("config/ResVersion")!.InnerText);
        var manifest = NteResListParser.Parse(NteResListDecryptor.Decrypt(File.ReadAllBytes(Path.Combine(Sdk, "ResList.xml"))));
        Assert.Equal("1.4.17", manifest.Version);
        Assert.Equal("game.pak", Assert.Single(manifest.Resources).Filename);
        string backup = Assert.Single(Directory.GetDirectories(backups));
        Assert.Equal(old, File.ReadAllText(Path.Combine(backup, "config.xml")));
        Assert.Equal("old manifest", File.ReadAllText(Path.Combine(backup, "ResList.xml")));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
