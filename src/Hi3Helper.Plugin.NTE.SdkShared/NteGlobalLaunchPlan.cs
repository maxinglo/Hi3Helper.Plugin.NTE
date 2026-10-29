using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nte.Login;

// Resolves only a direct game launch. Installed vendor files supply static
// configuration and the game SDK; this plan creates no vendor process.
internal sealed record NteGlobalLaunchPlan(string GameRoot, string GameExecutable, string GameArguments)
{
    internal static NteGlobalLaunchPlan Create(NteHostLaunchOptions options)
    {
        if (options.Region != NteHostRegion.Global) throw new ArgumentException("A global launch context is required.");
        string root = Path.GetFullPath(options.GameRoot);
        string platform = Path.Combine(root, "NTEGlobal");
        string config = Path.Combine(platform, "Config", "Config.ini");
        foreach (string file in new[]
                 {
                     config, Path.Combine(platform, "NTEGlobalGame.exe"),
                     Path.Combine(platform, "WPGameClientSDK_64.dll"),
                     Path.Combine(platform, "ResData", "3000001.dat"),
                     Path.Combine(root, "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe"),
                     Path.Combine(root, "Client/WindowsNoEditor/HT/Binaries/Win64/WPGlobalSDK_64.dll")
                 })
            if (!File.Exists(file))
                throw new NteGlobalLaunchException("GlobalFilesMissing");
        if (IniValue(ReadBounded(config), "General", "GameID") != "3000001")
            throw new NteGlobalLaunchException("GlobalWrongPlatform");
        string marker = Path.Combine(root, ".collapse_nte_config");
        if (File.Exists(marker))
        {
            string? appId = IniValue(ReadBounded(marker), "", "AppId");
            if (appId != "3000001")
                throw new NteGlobalLaunchException("GlobalWrongRegion");
        }

        return new(root,
            Path.GetFullPath(Path.Combine(root, "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe")),
            CreateGameArguments(options));
    }

    private static string CreateGameArguments(NteHostLaunchOptions options)
    {
        // The installed Global component uses Saved_Global. Without this
        // flag the same game executable reads the mainland Saved directory.
        foreach (Match match in Regex.Matches(options.AdditionalArguments ?? "", "(?:[^\\s\"]|\"[^\"]*\")+"))
        {
            string argument = match.Value.Trim('"');
            if (!argument.StartsWith('-')) continue;
            int equals = argument.IndexOf('=');
            string name = (equals < 0 ? argument : argument[..equals]).TrimStart('-');
            if (name.Equals("enginesaveddirsuffix", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("saveddirsuffix", StringComparison.OrdinalIgnoreCase) &&
                (equals < 0 || !argument[(equals + 1)..].Trim('"')
                    .Equals("Global", StringComparison.OrdinalIgnoreCase)))
                throw new NteGlobalLaunchException("GlobalSaveDirectoryConflict");
        }

        string required = NteHostLaunchOptions.RequiredGameArguments + " --saveddirsuffix=Global";
        return string.IsNullOrWhiteSpace(options.AdditionalArguments)
            ? required
            : required + " " + options.AdditionalArguments;
    }

    internal ProcessStartInfo CreateStartInfo(string pipeName, string deviceId,
        IReadOnlyDictionary<string, string> platformEnvironment)
    {
        if (!Guid.TryParseExact(deviceId, "N", out _))
            throw new ArgumentException("Invalid global device context.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
            pipeName.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_"))
            throw new ArgumentException("Invalid game pipe name.", nameof(pipeName));
        string platform = Path.Combine(GameRoot, "NTEGlobal");
        ProcessStartInfo start = new(GameExecutable, GameArguments)
        {
            WorkingDirectory = Path.Combine(GameRoot, "Client"), UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string key in new[]
                 {
                     "ArcGameP", "ArcGameSDKSharePath", "ArcGameAppId", "ArcGameAppAbbr", "ArcProcessId",
                     "ArcMoblieNotify", "anchor_w", "anchor_v", "ArcMId", "ArcMMId", "ArcDataIndex", "GameVersion",
                     "_PATCHER_RES_VERSION_", "_PATCHER_CONFIG_PATH_", "_PATCHER_USERDATA_PATH_", "_PATCHER_LOG_PATH_"
                 }) start.Environment.Remove(key);
        if (!platformEnvironment.TryGetValue("ArcGameAppId", out string? appId) || appId != "3000001")
            throw new ArgumentException("Invalid global platform application.", nameof(platformEnvironment));
        // These must exist before the first native call: IsStartFromPlatform
        // caches its initial check, and a later environment ACK cannot fix it.
        foreach (string key in new[]
                     { "ArcGameAppId", "ArcProcessId", "ArcMoblieNotify", "anchor_w", "anchor_v", "ArcMId", "ArcMMId" })
        {
            if (!platformEnvironment.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) ||
                value.Length > 260 || value.Contains('\0'))
                throw new ArgumentException("Incomplete global platform context.", nameof(platformEnvironment));
            start.Environment[key] = value;
        }

        // This host owns row zero; the SDK indexes it as row XOR app ID.
        start.Environment["ArcDataIndex"] = appId;
        start.Environment["ArcGameP"] = pipeName;
        // QOneSdkConfigHelper supplies the same device identifier to its
        // HTTP metadata and the child's SDK. Keep it out of command lines.
        start.Environment["ArcDummyMoblieDeviceId"] = deviceId;
        start.Environment["ArcGameSDKSharePath"] = Path.Combine(platform, "WPGameClientSDK");
        start.Environment["GamePlatformPath"] = platform;
        start.Environment["_PATCHER_RES_PATH_"] = GameRoot;
        start.Environment["_PATCHER_SDK_PATH_"] = Path.Combine(platform, "UserData", "Patcher", "PatcherSDK");
        // The game reads these public selectors during its first SDK calls,
        // before the pipe has delivered the channel configuration.
        start.Environment["channelId"] = "4";
        start.Environment["subChannelId"] = "11";
        start.Environment["mediaId"] = "11";
        foreach (string flag in new[] { "wmgplaunch", "ArcGameMsgSkipFlag" }) start.Environment[flag] = "1";
        // The fixed official component selects 2 for the Global SDK. The
        // mainland selector 1 changes how the game resolves its services.
        start.Environment["ArcGameMobileSdkType"] = "2";
        start.Environment["sdkLogPath"] = "NUL";
        return start;
    }

    private static string? IniValue(string text, string section, string key)
    {
        string current = "";
        string? result = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                continue;
            }

            int equals = line.IndexOf('=');
            if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && equals > 0 &&
                line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                result = line[(equals + 1)..].Trim();
        }

        return result;
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length > 1024 * 1024) throw new NteGlobalLaunchException("GlobalConfigInvalid");
        byte[] data = new byte[file.Length];
        file.ReadExactly(data);
        return data;
    }

    private static string ReadBounded(string path)
    {
        byte[] data = ReadBoundedBytes(path);
        Encoding encoding = DetectEncoding(data, out int preamble);
        try
        {
            return encoding.GetString(data, preamble, data.Length - preamble);
        }
        catch (DecoderFallbackException)
        {
            throw new NteGlobalLaunchException("GlobalConfigInvalid");
        }
    }

    private static Encoding DetectEncoding(byte[] data, out int preamble)
    {
        preamble = 0;
        if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) preamble = 3;
        else if (data.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            preamble = 2;
            return new UnicodeEncoding(false, false, true);
        }
        else if (data.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            preamble = 2;
            return new UnicodeEncoding(true, false, true);
        }

        return new UTF8Encoding(false, true);
    }
}

internal sealed class NteGlobalLaunchException(string key) : InvalidOperationException(key)
{
    internal string Key { get; } = key;
}
