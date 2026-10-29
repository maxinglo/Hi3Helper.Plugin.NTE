using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Hi3Helper.Plugin.NTE.Management.Config;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Persist the official SDK state only after the selected game resources have been verified.</summary>
internal static class NteOfficialPatcherState
{
    private const string SdkDirectory = "NTELauncher/UserData/Patcher/PatcherSDK";
    private static readonly byte[] RemoteKey = "20991231@patcher"u8.ToArray();
    private static readonly byte[] RemoteIv = "2099123123590000"u8.ToArray();

    internal static XmlDocument ParseXml(string xml)
    {
        if (xml.StartsWith('\uFEFF')) xml = xml[1..];
        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XmlDocument doc = new() { XmlResolver = null };
        doc.Load(reader);
        return doc;
    }

    internal static byte[] SelectInstalledBaseVersions(byte[] remoteManifest, string? gameRoot,
        NteRegionConfig? region = null)
    {
        XmlDocument remote = ParseXml(Encoding.UTF8.GetString(remoteManifest));
        XmlElement root = remote.DocumentElement ?? throw new InvalidDataException("Missing resource manifest.");
        if (root.Name != "ResList") throw new InvalidDataException("Invalid remote resource manifest.");
        foreach (XmlNode node in root.SelectNodes("BaseVersion")!) root.RemoveChild(node);
        region ??= NteConfigProvider.CN;
        string? localPath = gameRoot == null ? null : Path.Combine(gameRoot, region.SdkDirectory, "ResList.xml");
        if (localPath != null && File.Exists(localPath))
        {
            XmlDocument installed =
                ParseXml(Encoding.UTF8.GetString(
                    NteResListDecryptor.Decrypt(File.ReadAllBytes(localPath), region.AppId)));
            foreach (XmlNode node in installed.DocumentElement!.SelectNodes("BaseVersion")!)
                root.AppendChild(remote.ImportNode(node, true));
        }

        return Encoding.UTF8.GetBytes(remote.OuterXml);
    }

    internal static byte[] EncodeRemoteConfiguration(string xml)
        => Encrypt(Compress(Encoding.UTF8.GetBytes(xml)), RemoteKey, RemoteIv);

    internal static string DecodeRemoteConfiguration(string encoded)
        => Encoding.UTF8.GetString(Decompress(Decrypt(Convert.FromBase64String(encoded), RemoteKey, RemoteIv)));

    internal static byte[] CreateLocalManifest(byte[] selectedManifest, string appId, string? gameRoot = null)
    {
        XmlDocument doc = ParseXml(Encoding.UTF8.GetString(selectedManifest));
        XmlElement root = doc.DocumentElement!;
        // The SDK stores ordinary archive entries as individual installed resources. Optional base versions keep their own layout.
        foreach (XmlNode package in root.SelectNodes("Package")!)
        {
            foreach (XmlElement entry in package.SelectNodes("Pak/Entry")!)
            {
                XmlElement resource = doc.CreateElement("Res");
                resource.SetAttribute("filename", entry.GetAttribute("name"));
                resource.SetAttribute("filesize", entry.GetAttribute("size"));
                resource.SetAttribute("md5", entry.GetAttribute("md5"));
                if (entry.HasAttribute("timestamp"))
                    resource.SetAttribute("timestamp", entry.GetAttribute("timestamp"));
                root.AppendChild(resource);
            }

            root.RemoveChild(package);
        }

        if (gameRoot != null)
            foreach (XmlElement resource in root.SelectNodes("Res")!)
            {
                string path = NteGameInstaller.ResolveResourcePath(gameRoot, resource.GetAttribute("filename"));
                if (!File.Exists(path))
                    throw new IOException("Verified resource disappeared before official state commit: " + path);
                if (new FileInfo(path).Length != long.Parse(resource.GetAttribute("filesize"),
                        System.Globalization.CultureInfo.InvariantCulture))
                    throw new IOException("Verified resource changed before official state commit: " + path);
                resource.SetAttribute("timestamp",
                    new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds()
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

        byte[] plain = Encoding.UTF8.GetBytes(doc.OuterXml);
        string key = (appId + "@Patcher").PadRight(16, '0');
        if (key.Length != 16) throw new InvalidDataException("Unsupported official SDK application identifier.");
        byte[] encrypted = Encrypt(Compress(plain), Encoding.ASCII.GetBytes(key), "PatcherSDK000000"u8.ToArray());
        byte[] result = new byte[16 + encrypted.Length];
        "PatcherXML0\0"u8.CopyTo(result);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), plain.Length);
        encrypted.CopyTo(result, 16);
        return result;
    }

    internal static string CreateConfiguration(string currentXml, string releaseXml, string branch,
        byte[] selectedManifest)
    {
        XmlDocument local = ParseXml(currentXml);
        XmlDocument release = ParseXml(releaseXml);
        XmlElement target = release.DocumentElement ?? throw new InvalidDataException("Missing release configuration.");
        XmlElement root = local.DocumentElement ?? throw new InvalidDataException("Missing installed configuration.");
        if (root.Name != "config" || target.Name != "config")
            throw new InvalidDataException("Invalid official configuration root.");
        if (root.SelectSingleNode("LocalBranch")?.InnerText != branch)
            throw new InvalidDataException("Official game branch changed during the update.");

        void Set(string name, string value)
        {
            XmlNode node = root.SelectSingleNode(name) ?? root.AppendChild(local.CreateElement(name))!;
            node.InnerText = value;
        }

        foreach (string field in new[] { "ResVersion", "ResSize" })
            Set(field,
                target.SelectSingleNode(field)?.InnerText ??
                throw new InvalidDataException("Missing release field: " + field));
        XmlDocument manifest = ParseXml(Encoding.UTF8.GetString(selectedManifest));
        Set("ResCount",
            (manifest.DocumentElement!.SelectNodes("Res")!.Count +
             manifest.DocumentElement.SelectNodes("Package/Pak/Entry")!.Count)
            .ToString(System.Globalization.CultureInfo.InvariantCulture));
        XmlNode extra = root.SelectSingleNode("Extra") ?? root.AppendChild(local.CreateElement("Extra"))!;
        foreach (string name in new[] { "diffHash", "listHash" })
        {
            string value = target.SelectSingleNode("Extra/" + name)?.InnerText ??
                           throw new InvalidDataException("Missing release hash: " + name);
            XmlNode node = extra.SelectSingleNode(name) ?? extra.AppendChild(local.CreateElement(name))!;
            node.InnerText = value;
        }

        Set("RemoteContent", Convert.ToBase64String(EncodeRemoteConfiguration(releaseXml)));
        return local.OuterXml;
    }

    internal static void Commit(string gameRoot, string branch, string appId, string releaseXml,
        byte[] originalRemoteManifest, byte[] selectedManifest, CancellationToken token, string? backupDirectory = null)
    {
        NteRegionConfig region = NteConfigProvider.GetRegion(appId);
        string directory = Path.Combine(gameRoot, region.SdkDirectory);
        string configPath = Path.Combine(directory, "config.xml");
        if (!File.Exists(configPath) && !region.IsTaiwan)
            return; // Independent CN/Global login does not require official state.
        EnsureOfficialLauncherClosed(gameRoot, region);
        XmlDocument release = ParseXml(releaseXml);
        string expected = release.SelectSingleNode("config/Extra/listHash")?.InnerText ??
                          throw new InvalidDataException("Missing official manifest identity.");
        if (!Convert.ToHexString(MD5.HashData(originalRemoteManifest))
                .Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official resource manifest differs from the release configuration.");
        string targetVersion = release.SelectSingleNode("config/ResVersion")?.InnerText ?? "";
        if (ParseXml(Encoding.UTF8.GetString(selectedManifest)).DocumentElement?.GetAttribute("version") !=
            targetVersion)
            throw new InvalidDataException("Cannot commit official state for a different game version.");
        string manifestPath = Path.Combine(directory, "ResList.xml");
        if (region.IsTaiwan && (!File.Exists(configPath) || !File.Exists(manifestPath)))
        {
            string current;
            if (File.Exists(configPath)) current = File.ReadAllText(configPath);
            else
            {
                XmlDocument seed = ParseXml(releaseXml);
                XmlElement local = seed.CreateElement("LocalBranch");
                local.InnerText = branch;
                seed.DocumentElement!.AppendChild(local);
                current = seed.OuterXml;
            }

            byte[] installed = CreateLocalManifest(selectedManifest, appId, gameRoot);
            byte[] configuration =
                Encoding.UTF8.GetBytes(CreateConfiguration(current, releaseXml, branch, selectedManifest));
            token.ThrowIfCancellationRequested();
            NteSharedInstallation.WriteAtomic(manifestPath, installed);
            NteSharedInstallation.WriteAtomic(configPath, configuration);
            return;
        }

        byte[] oldConfig = File.ReadAllBytes(configPath);
        byte[] oldManifest = File.ReadAllBytes(manifestPath);
        byte[] newManifest = CreateLocalManifest(selectedManifest, appId, gameRoot);
        string newConfig =
            CreateConfiguration(Encoding.UTF8.GetString(oldConfig), releaseXml, branch, selectedManifest);
        string backup =
            Path.Combine(
                backupDirectory ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CollapseLauncher", "Plugins", "NTE", "StateBackups"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "config.xml"), oldConfig);
        File.WriteAllBytes(Path.Combine(backup, "ResList.xml"), oldManifest);
        token.ThrowIfCancellationRequested();
        try
        {
            Replace(manifestPath, newManifest);
            Replace(configPath, Encoding.UTF8.GetBytes(newConfig));
        }
        catch
        {
            Replace(manifestPath, oldManifest);
            Replace(configPath, oldConfig);
            throw;
        }
    }

    internal static void EnsureOfficialLauncherClosed(string gameRoot, NteRegionConfig? region = null)
    {
        region ??= NteConfigProvider.CN;
        if (!File.Exists(Path.Combine(gameRoot, region.SdkDirectory, "config.xml"))) return;
        string[] names = region.IsTaiwan ? ["NTETWGame", "NTETWUpdate", "NTETWLauncher"] :
            region.IsGlobal ? ["NTEGlobalGame", "NTEGlobalUpdate", "NTEGlobalLauncher"] :
            ["NTEGame", "NTEUpdate", "NTELauncher"];
        foreach (string name in names)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length != 0)
                    throw new IOException(
                        "Exit the official NTE launcher before updating or repairing the game with Collapse.");
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }
    }

    private static void Replace(string path, byte[] bytes)
    {
        string temp = path + ".collapse-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static byte[] Compress(byte[] bytes)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, CompressionLevel.Optimal, true)) stream.Write(bytes);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        using ZLibStream stream = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] bytes, byte[] key, byte[] iv)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        using ICryptoTransform transform = aes.CreateEncryptor();
        return transform.TransformFinalBlock(bytes, 0, bytes.Length);
    }

    private static byte[] Decrypt(byte[] bytes, byte[] key, byte[] iv)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        using ICryptoTransform transform = aes.CreateDecryptor();
        return transform.TransformFinalBlock(bytes, 0, bytes.Length);
    }
}
