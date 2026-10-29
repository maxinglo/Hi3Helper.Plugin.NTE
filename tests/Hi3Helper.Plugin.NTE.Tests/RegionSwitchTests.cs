using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Nte.Login;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class RegionSwitchTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "NteRegionSwitchTests", Guid.NewGuid().ToString("N"));

    private string Index => Path.Combine(root, "test-index.json");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private string Put(string relative, byte[] bytes)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void TaiwanIsItsOwnPresetAndUsesItsOwnOfficialLogin()
    {
        var tw = new Hi3Helper.Plugin.NTE.Management.PresetConfig.NteCNPresetConfig(NteConfigProvider.Taiwan);
        Assert.Equal("NteTaiwan", tw.ProfileName);
        Assert.Equal("2000013", Assert.IsType<NteGameManager>(tw.GameManager).AppId);
        Assert.Equal(new[] { "zh-TW" }, tw.SupportedLanguages);
        Put("NTETW/NTETWGame.exe", Bytes("fixture; never executed"));
        var plan = NteOfficialLaunchPlan.Create(root, NteHostRegion.Taiwan);
        Assert.EndsWith(Path.Combine("NTETW", "NTETWGame.exe"), plan.Executable);
        Assert.Contains("/autoplay", plan.CreateStartInfo(null).Arguments);
        var elevated = plan.CreateStartInfo("-dx11 -log=\"a b&c\"", requestElevation: true);
        Assert.True(elevated.UseShellExecute);
        Assert.Equal("runas", elevated.Verb);
        Assert.Equal(plan.Executable, elevated.FileName);
        Assert.Equal("/launcher /directly /autoplay -dx11 -log=\"a b&c\"", elevated.Arguments);
        var options = new NteHostLaunchOptions(root, Region: NteHostRegion.Taiwan);
        Assert.Equal(NteHostRegion.Taiwan, NteHostLaunchOptions.Parse(options.ToArguments()).Region);
    }

    [Fact]
    public async Task RoundTrip_ReusesCommonDataAndRestoresCachedSignedPrograms()
    {
        using Fixture fixture = new(this);
        using (HttpClient client = new(fixture, false))
            Assert.True((await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Taiwan,
                null, TestContext.Current.CancellationToken, indexPath: Index)).Completed);
        Assert.Equal("taiwan-program",
            File.ReadAllText(Path.Combine(root, NteConfigProvider.GameExecutableRelativePath)));
        Assert.False(File.Exists(Path.Combine(root, "Client/global.hpw")));
        Assert.Equal("shared-map", File.ReadAllText(Path.Combine(root, "Client/shared.pak")));
        Assert.Equal("2000013", NteConfigProvider.DetectInstalledRegion(root)!.AppId);
        Assert.Equal(root, NteSharedInstallation.FindRoot(NteConfigProvider.Global, Index));
        Assert.Equal(root, NteSharedInstallation.FindRoot(NteConfigProvider.Taiwan, Index));
        int before = fixture.ResourceRequests;
        using (HttpClient client = new(fixture, false))
            await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Global,
                null, TestContext.Current.CancellationToken, indexPath: Index);
        Assert.Equal("global-program",
            File.ReadAllText(Path.Combine(root, NteConfigProvider.GameExecutableRelativePath)));
        Assert.Equal("global-runtime", File.ReadAllText(Path.Combine(root, "Client/global.hpw")));
        Assert.False(File.Exists(Path.Combine(root, "Client/taiwan.sys")));
        Assert.Equal(before, fixture.ResourceRequests);
        Assert.Equal("3000001", NteSharedInstallation.Load(root)!.ActiveAppId);
        Assert.False(NteSharedInstallation.HasPendingSwitch(root));
    }

    [Fact]
    public async Task PreviewDoesNotActivateOrWriteSharedState()
    {
        using Fixture fixture = new(this);
        byte[] marker = File.ReadAllBytes(Path.Combine(root, ".collapse_nte_config"));
        using HttpClient client = new(fixture, false);
        var plan = await new NteRegionSwitchService(client, _ => { }).PlanAsync(root, NteConfigProvider.Taiwan,
            null, TestContext.Current.CancellationToken);
        Assert.True(plan.ReusedFiles > 0);
        Assert.True(plan.DownloadBytes > 0);
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(root, ".collapse_nte_config")));
        Assert.False(File.Exists(NteSharedInstallation.StatePath(root)));
        Assert.Equal("global-program",
            File.ReadAllText(Path.Combine(root, NteConfigProvider.GameExecutableRelativePath)));
    }

    [Fact]
    public async Task CancellationDuringActivationRestoresSourceAndKeepsDownloadedObjects()
    {
        using Fixture fixture = new(this);
        using CancellationTokenSource cancel = new();
        using HttpClient client = new(fixture, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NteRegionSwitchService(client, _ => { }).SwitchAsync(root,
                NteConfigProvider.Taiwan, (phase, _, _, _, _, _) =>
                {
                    if (phase == 3) cancel.Cancel();
                }, cancel.Token, indexPath: Index));
        Assert.Equal("global-program",
            File.ReadAllText(Path.Combine(root, NteConfigProvider.GameExecutableRelativePath)));
        Assert.Equal("3000001", NteConfigProvider.DetectInstalledRegion(root)!.AppId);
        Assert.False(File.Exists(NteSharedInstallation.StatePath(root)));
        Assert.False(NteSharedInstallation.HasPendingSwitch(root));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, ".collapse-nte/objects"), "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RecoveryRestoresAnInterruptedRenameAndItsOriginalState()
    {
        using Fixture fixture = new(this);
        string game = NteConfigProvider.GameExecutableRelativePath;
        byte[] target = Bytes("new program");
        string staged = NteSharedInstallation.ObjectPath(root, game, Hash(target), target.Length);
        Put(Path.GetRelativePath(root, staged), target);
        var tx = NteRegionSwitchTransaction.Begin(root, "3000001", "2000013");
        await tx.ReplaceAsync(game, staged, target.Length, Hash(target), TestContext.Current.CancellationToken);
        Assert.True(NteSharedInstallation.HasPendingSwitch(root));
        Assert.True(NteRegionSwitchTransaction.Recover(root));
        Assert.Equal("global-program", File.ReadAllText(Path.Combine(root, game)));
        Assert.Equal(target, File.ReadAllBytes(staged));
        Assert.False(NteSharedInstallation.HasPendingSwitch(root));
    }

    [Fact]
    public async Task RecoveryPreservesExternalEditsInsteadOfOverwritingThem()
    {
        using Fixture fixture = new(this);
        string game = NteConfigProvider.GameExecutableRelativePath;
        byte[] target = Bytes("new program");
        string staged = NteSharedInstallation.ObjectPath(root, game, Hash(target), target.Length);
        Put(Path.GetRelativePath(root, staged), target);
        var tx = NteRegionSwitchTransaction.Begin(root, "3000001", "2000013");
        await tx.ReplaceAsync(game, staged, target.Length, Hash(target), TestContext.Current.CancellationToken);
        Put(game, Bytes("outside modification"));
        Assert.Throws<IOException>(() => NteRegionSwitchTransaction.Recover(root));
        Assert.Equal("outside modification", File.ReadAllText(Path.Combine(root, game)));
        Assert.True(NteSharedInstallation.HasPendingSwitch(root));
    }

    [Fact]
    public async Task FutureRegionForkOfAMapIsCachedAndReversible()
    {
        using Fixture fixture = new(this, forkMap: true);
        using (HttpClient client = new(fixture, false))
            await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Taiwan, null,
                TestContext.Current.CancellationToken, indexPath: Index);
        Assert.Equal("taiwan-map-v2", File.ReadAllText(Path.Combine(root, "Client/shared.pak")));
        int requests = fixture.ResourceRequests;
        using (HttpClient client = new(fixture, false))
            await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Global, null,
                TestContext.Current.CancellationToken, indexPath: Index);
        Assert.Equal("shared-map", File.ReadAllText(Path.Combine(root, "Client/shared.pak")));
        Assert.Equal(requests, fixture.ResourceRequests);
    }

    [Theory]
    [InlineData("Client/../NTETW/UserData/account.dat")]
    [InlineData("Client/../../escape")]
    public void ManifestTraversalIsRejected(string path)
    {
        string xml =
            $"<ResList version=\"1.4.6\"><Res filename=\"{path}\" filesize=\"1\" md5=\"{new string('a', 32)}\"/></ResList>";
        Assert.Throws<InvalidDataException>(() => NteRegionSwitchManifest.Read(Bytes(xml)));
    }

    [Fact]
    public void TagSelectionUsesTargetNodesRatherThanCopyingSourceVersions()
    {
        const string target =
            "<ResList version=\"1.4.6\"><BaseVersion tag=\"pakchunk101\" version=\"0.101.6\"><ResList/></BaseVersion><BaseVersion tag=\"pakchunk102\" version=\"0.102.6\"><ResList/></BaseVersion></ResList>";
        const string source =
            "<ResList version=\"1.4.10\"><BaseVersion tag=\"pakchunk101\" version=\"0.101.10\"><ResList/></BaseVersion></ResList>";
        string selected = Encoding.UTF8.GetString(NteRegionSwitchManifest.SelectTags(Bytes(target), source));
        Assert.Contains("0.101.6", selected);
        Assert.DoesNotContain("0.101.10", selected);
        Assert.DoesNotContain("pakchunk102", selected);
    }

    [Fact]
    public async Task InactiveProfileCannotOverwriteTheActiveRegionMarker()
    {
        using Fixture fixture = new(this);
        using (HttpClient client = new(fixture, false))
            await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Taiwan, null,
                TestContext.Current.CancellationToken, indexPath: Index);
        byte[] marker = File.ReadAllBytes(Path.Combine(root, ".collapse_nte_config"));
        NteGameManager global = new(region: NteConfigProvider.Global);
        global.SetGamePath(root);
        global.LoadConfig();
        global.IsGameInstalled(out bool installed);
        Assert.True(installed);
        global.SaveConfig();
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(root, ".collapse_nte_config")));
        global.Dispose();
    }

    [Fact]
    public async Task RemovingOneRegionPreservesTheActiveGameAndUnrelatedUserFiles()
    {
        using Fixture fixture = new(this);
        using (HttpClient client = new(fixture, false))
            await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Taiwan, null,
                TestContext.Current.CancellationToken, indexPath: Index);
        Put("NTEGlobal/UserData/unrelated-fixture.txt", Bytes("preserve this"));
        NteSharedInstallation.RemoveProfile(root, NteConfigProvider.Global, Index);
        Assert.Single(NteSharedInstallation.Load(root)!.Profiles);
        Assert.Equal("taiwan-program",
            File.ReadAllText(Path.Combine(root, NteConfigProvider.GameExecutableRelativePath)));
        Assert.Equal("shared-map", File.ReadAllText(Path.Combine(root, "Client/shared.pak")));
        Assert.Equal("preserve this", File.ReadAllText(Path.Combine(root, "NTEGlobal/UserData/unrelated-fixture.txt")));
        Assert.Null(NteSharedInstallation.FindRoot(NteConfigProvider.Global, Index));
        Assert.Equal(root, NteSharedInstallation.FindRoot(NteConfigProvider.Taiwan, Index));
    }

    [Fact]
    public async Task OrdinaryUpdatePreservesAFileStillNeededByTheInactiveRegion()
    {
        using Fixture fixture = new(this);
        using (HttpClient client = new(fixture, false))
            await new NteRegionSwitchService(client, _ => { }).SwitchAsync(root, NteConfigProvider.Taiwan, null,
                TestContext.Current.CancellationToken, indexPath: Index);
        string output = Path.Combine(root, "Client/shared.pak");
        byte[] original = File.ReadAllBytes(output), updated = Bytes("map after next patch");
        string temporary = Put("Client/shared.pak.new.tmp", updated);
        NteGameInstaller.CompleteDownloadedFile(temporary, output, updated.Length, Hash(updated),
            TestContext.Current.CancellationToken);
        Assert.Equal(updated, File.ReadAllBytes(output));
        string cached = NteSharedInstallation.ObjectPath(root, "Client/shared.pak", Hash(original), original.Length);
        Assert.Equal(original, File.ReadAllBytes(cached));
    }

    [Fact]
    public async Task FreshTaiwanLoginComponentsAndSdkStatePreserveBothAccountStores()
    {
        using Fixture fixture = new(this);
        using HttpClient client = new(fixture, false);
        byte[] globalState = File.ReadAllBytes(Path.Combine(root, NteConfigProvider.Global.SdkDirectory, "config.xml"));
        string account = Put("NTETW/UserData/Accounts/private.fixture", Bytes("synthetic account data"));
        await new NteOfficialLauncherInstaller(client).InstallAsync(root, NteConfigProvider.Taiwan, null,
            TestContext.Current.CancellationToken);
        Assert.Equal("launcher 2000013", File.ReadAllText(Path.Combine(root, "NTETW/NTETWGame.exe")));
        int requests = fixture.LauncherRequests;
        await new NteOfficialLauncherInstaller(client).InstallAsync(root, NteConfigProvider.Taiwan, null,
            TestContext.Current.CancellationToken);
        Assert.Equal(requests, fixture.LauncherRequests);
        byte[] game = Bytes("taiwan-program");
        Put(NteConfigProvider.GameExecutableRelativePath, game);
        byte[] manifest = Bytes(NteOfficialPatcherState
            .ParseXml(
                $"<ResList version=\"1.4.6\"><Res filename=\"{NteConfigProvider.GameExecutableRelativePath.Replace('\\', '/')}\" filesize=\"{game.Length}\" md5=\"{Hash(game)}\"/></ResList>")
            .OuterXml);
        string release =
            $"<config><ResVersion>1.4.6</ResVersion><ResSize>{game.Length}</ResSize><Extra><listHash>{Hash(manifest)}</listHash><diffHash>{new string('b', 32)}</diffHash></Extra></config>";
        NteOfficialPatcherState.Commit(root, "PC_140", "2000013", release, manifest, manifest,
            TestContext.Current.CancellationToken);
        string sdk = Path.Combine(root, NteConfigProvider.Taiwan.SdkDirectory);
        Assert.Equal("1.4.6",
            NteOfficialPatcherState.ParseXml(File.ReadAllText(Path.Combine(sdk, "config.xml")))
                .SelectSingleNode("config/ResVersion")!.InnerText);
        Assert.NotEmpty(NteResListDecryptor.Decrypt(File.ReadAllBytes(Path.Combine(sdk, "ResList.xml")), "2000013"));
        Assert.Equal("synthetic account data", File.ReadAllText(account));
        Assert.Equal(globalState,
            File.ReadAllBytes(Path.Combine(root, NteConfigProvider.Global.SdkDirectory, "config.xml")));
    }

    [Fact]
    public async Task CorruptOfficialLoginComponentCannotActivateAnyPreparedFiles()
    {
        using Fixture fixture = new(this) { CorruptLauncher = true };
        using HttpClient client = new(fixture, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => new NteOfficialLauncherInstaller(client).InstallAsync(root,
            NteConfigProvider.Taiwan, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(root, "NTETW/NTETWGame.exe")));
        Assert.False(File.Exists(Path.Combine(root, "NTETW/ResFilesM/2000013/PatcherConfig/PatcherConfig.json")));
        Assert.Equal("global-program",
            File.ReadAllText(Path.Combine(root, NteConfigProvider.GameExecutableRelativePath)));
    }

    [Fact]
    public async Task UnsupportedRegionCannotRecoverOrModifyAPendingSwitch()
    {
        Put(".collapse-nte/switch.json", Bytes("{}"));
        using HttpClient client = new();
        NteRegionSwitchService service = new(client, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PlanAsync(root, NteConfigProvider.CN, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SwitchAsync(root, NteConfigProvider.CN, null, TestContext.Current.CancellationToken));
        Assert.Equal("{}", File.ReadAllText(NteSharedInstallation.JournalPath(root)));
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> responses = new(StringComparer.OrdinalIgnoreCase);
        public int ResourceRequests { get; private set; }
        public int LauncherRequests { get; private set; }
        public bool CorruptLauncher { get; set; }

        internal Fixture(RegionSwitchTests test, bool forkMap = false)
        {
            foreach (var region in new[] { NteConfigProvider.Global, NteConfigProvider.Taiwan })
            {
                bool tw = region.IsTaiwan;
                string version = tw ? "1.4.6" : "1.4.10";
                Dictionary<string, byte[]> files = new()
                {
                    [NteConfigProvider.GameExecutableRelativePath.Replace('\\', '/')] =
                        Bytes(tw ? "taiwan-program" : "global-program"),
                    ["Client/shared.pak"] = Bytes(tw && forkMap ? "taiwan-map-v2" : "shared-map"),
                    [tw ? "Client/taiwan.sys" : "Client/global.hpw"] = Bytes(tw ? "taiwan-driver" : "global-runtime")
                };
                string manifest = "<ResList version=\"" + version + "\" tag=\"baseTag\">" + string.Join("",
                                      files.Select(pair =>
                                          $"<Res filename=\"{pair.Key}\" filesize=\"{pair.Value.Length}\" md5=\"{Hash(pair.Value)}\"/>")) +
                                  "</ResList>";
                byte[] plain = Bytes(NteOfficialPatcherState.ParseXml(manifest).OuterXml);
                string release =
                    $"<config><AppVersion>0.0</AppVersion><ResVersion>{version}</ResVersion><ResSize>{files.Sum(pair => pair.Value.Length)}</ResSize><Extra><listHash>{Hash(plain)}</listHash><diffHash>{new string('b', 32)}</diffHash></Extra></config>";
                string patcher = JsonSerializer.Serialize(new
                    { appId = region.AppId, branchName = "PC_140", gameResUrl = region.ResourceUrls });
                string main = region.LauncherDirectoryName + "Game.exe";
                Dictionary<string, byte[]> launcher = new()
                {
                    ["ResFilesM/" + region.AppId + "/PatcherConfig/PatcherConfig.json"] = Bytes(patcher),
                    [main] = Bytes("launcher " + region.AppId)
                };
                string allFiles = "<All_Files>" + string.Join("",
                                      launcher.Select(pair =>
                                          $"<File Path=\"/{pair.Key}\" Size=\"{pair.Value.Length}\" Checksum=\"{Hash(pair.Value)}\"/>")) +
                                  "</All_Files>";
                string origin = new Uri(region.LauncherVersionIniUrl).GetLeftPart(UriPartial.Authority);
                responses[region.LauncherVersionIniUrl] = Bytes("FileListURL=" + origin + "/launcher/1/AllFiles.xml");
                responses[origin + "/launcher/1/AllFiles.xml"] = Bytes(allFiles);
                foreach (var pair in launcher)
                    responses[origin + "/launcher/1/" + pair.Key + ".zip"] = Zip(pair.Key.Split('/')[^1], pair.Value);
                responses[region.ResourceUrls[0] + "/PC_140/Version/Windows/config.xml"] = Bytes(release);
                responses[region.ResourceUrls[0] + "/PC_140/Version/Windows/version/" + version + "/ResList.bin.zip"] =
                    Zip("ResList.bin", NteOfficialPatcherState.CreateLocalManifest(plain, region.AppId));
                foreach (var pair in files)
                    responses[
                        NteConfigProvider.BuildResourceUrl(region.ResourceUrls[0], "PC_140", Hash(pair.Value),
                            pair.Value.Length)] = pair.Value;
                if (!tw)
                {
                    foreach (var pair in files) test.Put(pair.Key, pair.Value);
                    foreach (var pair in launcher) test.Put(region.LauncherDirectoryName + "/" + pair.Key, pair.Value);
                    test.Put(region.SdkDirectory + "/config.xml",
                        Bytes(release.Replace("<config>", "<config><LocalBranch>PC_140</LocalBranch>")));
                    test.Put(region.SdkDirectory + "/ResList.xml",
                        NteOfficialPatcherState.CreateLocalManifest(plain, region.AppId));
                    test.Put(".collapse_nte_config",
                        Bytes("GameVersion=" + version + "\nAppId=" + region.AppId + "\n"));
                }
            }
        }

        private static byte[] Zip(string name, byte[] payload)
        {
            using MemoryStream bytes = new();
            using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, true))
            using (Stream entry = zip.CreateEntry(name).Open())
                entry.Write(payload);
            return bytes.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            url = url.Replace("https://patchnte2.iwplay.com.tw/", "https://patchnte1.iwplay.com.tw/",
                StringComparison.Ordinal);
            if (request.RequestUri.AbsolutePath.Contains("/Res/", StringComparison.Ordinal)) ResourceRequests++;
            if (!responses.TryGetValue(url, out byte[]? data))
                throw new InvalidOperationException("Unexpected fixture endpoint: " + url);
            if (url.Contains("/launcher/1/", StringComparison.Ordinal) &&
                url.EndsWith(".zip", StringComparison.Ordinal)) LauncherRequests++;
            if (CorruptLauncher && url.EndsWith("/NTETWGame.exe.zip", StringComparison.Ordinal))
                data = Zip("NTETWGame.exe", Bytes("corrupt"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
