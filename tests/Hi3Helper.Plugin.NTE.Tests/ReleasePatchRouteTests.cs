using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class ReleasePatchRouteTests
{
    [Fact]
    public void Official153Metadata_MatchesTheLauncherAndReusesAllNinePreviewDeltas()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReleaseTransition");
        byte[] diff = File.ReadAllBytes(Path.Combine(directory, "cn-1.5.3-lastdiff.xml"));
        NtePatchList patches = NtePatchList.Parse(diff,
            "<config><Extra><diffHash>f3effb028257b49ceb9f777afb9e0c3a</diffHash></Extra></config>");
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cn-1.4.18-to-1.5.3.json")));
        HashSet<string> cached = fixture.RootElement.GetProperty("cachedResources").EnumerateArray()
            .Select(n => n.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long full = 0, direct = 0, remaining = 0;
        int chains = 0;
        foreach (JsonElement row in fixture.RootElement.GetProperty("files").EnumerateArray())
        {
            NteGameInstaller.DownloadTask target = Descriptor(row.GetProperty("target"));
            JsonElement old = row.GetProperty("old");
            NteGameInstaller.DownloadTask? previous = old.ValueKind == JsonValueKind.Null ? null : Descriptor(old);
            NtePatchRoute? baseline = patches.FindRoute(target, previous, (_, _) => false, r => r.Filesize);
            NtePatchRoute? reused = patches.FindRoute(target, previous,
                (md5, size) => cached.Contains(NtePatchList.Identity(md5, size)),
                r => cached.Contains(NtePatchList.Identity(r.Md5, r.Filesize)) ? 0 : r.Filesize);
            full += target.TotalSize;
            direct += Math.Min(target.TotalSize, baseline?.NetworkBytes ?? target.TotalSize);
            remaining += Math.Min(target.TotalSize, reused?.NetworkBytes ?? target.TotalSize);
            if (reused?.Steps.Count == 2) chains++;
        }
        Assert.Equal(50_532_024_120, full);
        Assert.Equal(11_923_529_736, direct); // 11.10 GiB, matching the official launcher's screenshot.
        Assert.Equal(1_421_258_914, remaining); // Assumes the unmodified 1.4.18 manifest, not the partially updated local game.
        Assert.Equal(9, chains);
    }

    [Fact]
    public void RouteSearch_HandlesCyclesAndChoosesTheCheapestRemainingBytes()
    {
        string a = new('a', 32), b = new('b', 32), c = new('c', 32);
        string first = new('1', 32), second = new('2', 32), direct = new('3', 32), cycle = new('4', 32);
        byte[] xml = Encoding.UTF8.GetBytes($"<PatchList><Patch oldfile='{a}.100' newfile='{b}.100' patch='{first}.60'/>" +
            $"<Patch oldfile='{b}.100' newfile='{c}.100' patch='{second}.10'/>" +
            $"<Patch oldfile='{a}.100' newfile='{c}.100' patch='{direct}.50'/>" +
            $"<Patch oldfile='{b}.100' newfile='{a}.100' patch='{cycle}.5'/></PatchList>");
        NtePatchList patches = NtePatchList.Parse(xml,
            $"<config><Extra><diffHash>{Convert.ToHexString(MD5.HashData(xml))}</diffHash></Extra></config>");
        NteGameInstaller.DownloadTask source = new() { ExpectedMd5 = a, TotalSize = 100 };
        NteGameInstaller.DownloadTask target = new() { ExpectedMd5 = c, TotalSize = 100 };
        Assert.Single(patches.FindRoute(target, source, (_, _) => false, r => r.Filesize)!.Steps);
        NtePatchRoute route = patches.FindRoute(target, source, (_, _) => false, r => r.Md5 == first ? 0 : r.Filesize)!;
        Assert.Equal(10, route.NetworkBytes);
        Assert.Equal(new[] { first, second }, route.Steps.Select(s => s.Resource.Md5));
    }

    [Fact]
    public void ThreeStageChain_BudgetsAdjacentOutputsAndReleasesConsumedIntermediates()
    {
        string root = Path.Combine(Path.GetTempPath(), "NteChainTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[][] versions = [new byte[32], Enumerable.Repeat((byte)1, 48).ToArray(),
                Enumerable.Repeat((byte)2, 64).ToArray(), Enumerable.Repeat((byte)3, 80).ToArray()];
            string Hash(byte[] value) => Convert.ToHexStringLower(MD5.HashData(value));
            string path = Path.Combine(root, "game.dat");
            File.WriteAllBytes(path, versions[0]);
            NteFileVerificationCache verification = new();
            NteUpdateContext context = new(root, "1289", verification);
            List<NtePatchEntry> steps = [];
            for (int i = 1; i < versions.Length; i++)
            {
                byte[] patch = HdiffFixtures.Create(versions[i - 1], versions[i], []);
                string cache = context.PathFor(Hash(patch), patch.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                File.WriteAllBytes(cache, patch);
                verification.RememberVerified(cache, patch.Length, Hash(patch));
                steps.Add(new(Hash(versions[i - 1]), versions[i - 1].Length, Hash(versions[i]), versions[i].Length,
                    new() { Md5 = Hash(patch), Filesize = patch.Length }));
            }
            NteGameInstaller.DownloadTask target = new() { OutputPath = path, ExpectedMd5 = Hash(versions[^1]), TotalSize = versions[^1].Length };
            NteUpdateOperation operation = new(target, context, new(Hash(versions[0]), versions[0].Length, false, steps, 0));
            context.Register([operation]);
            Assert.Equal(144, operation.CaptureSpace().WorkBytes);
            long peak = 0;
            operation.Complete(TestContext.Current.CancellationToken, (_, _) => { }, _ => peak = Math.Max(peak, operation.ScratchBytes));
            Assert.Equal(144, peak);
            Assert.Equal(versions[^1], File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".collapse_nte_preload", "1289")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static NteGameInstaller.DownloadTask Descriptor(JsonElement value) => new()
        { ExpectedMd5 = value.GetProperty("md5").GetString()!, TotalSize = value.GetProperty("size").GetInt64() };

    [Fact]
    public void SharedPatchAndOutput_AreKeptUntilBothTargetsHaveBeenVerified()
    {
        string root = Path.Combine(Path.GetTempPath(), "NteSharedPatchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] old = new byte[32], target = Enumerable.Repeat((byte)1, 80).ToArray();
            byte[] patch = HdiffFixtures.Create(old, target, []);
            string Hash(byte[] value) => Convert.ToHexStringLower(MD5.HashData(value));
            NteFileVerificationCache verification = new();
            NteUpdateContext context = new(root, "1289", verification);
            string cachedPatch = context.PathFor(Hash(patch), patch.Length), cachedOutput = context.PathFor(Hash(target), target.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPatch)!);
            File.WriteAllBytes(cachedPatch, patch);
            verification.RememberVerified(cachedPatch, patch.Length, Hash(patch));
            NtePatchRoute route = new(Hash(old), old.Length, false,
                [new(Hash(old), old.Length, Hash(target), target.Length, new() { Md5 = Hash(patch), Filesize = patch.Length })], 0);
            NteUpdateOperation[] operations = Enumerable.Range(0, 2).Select(i =>
            {
                string path = Path.Combine(root, i + ".dat");
                File.WriteAllBytes(path, old);
                return new NteUpdateOperation(new() { OutputPath = path, ExpectedMd5 = Hash(target), TotalSize = target.Length }, context, route);
            }).ToArray();
            context.Register(operations);
            NteDownloadSpace space = operations[0].CaptureSpace();
            Assert.Equal(160, space.WorkBytes); // Cached output plus the verified destination copy.
            Assert.Equal(32, space.ReclaimedBytes); // Both shared objects remain leased after this target.
            operations[0].Complete(TestContext.Current.CancellationToken, (_, _) => { }, _ => { });
            Assert.True(File.Exists(cachedPatch));
            Assert.True(File.Exists(cachedOutput));
            Assert.Equal(old, File.ReadAllBytes(operations[1].Target.OutputPath));
            operations[1].Complete(TestContext.Current.CancellationToken, (_, _) => { }, _ => { });
            Assert.All(operations, op => Assert.Equal(target, File.ReadAllBytes(op.Target.OutputPath)));
            Assert.False(File.Exists(cachedPatch));
            Assert.False(File.Exists(cachedOutput));
        }
        finally { Directory.Delete(root, true); }
    }
}
