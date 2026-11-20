using System;
using System.IO;
using System.Text.Json;

namespace Nte.Login;

internal sealed class NteLoginNotice(string path, int revision = NteLoginNotice.CurrentRevision)
{
    internal const int CurrentRevision = 2;
    internal const int BilibiliRevision = 1;
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Collapse", "NTE", "login-notice.json");
    internal static string BilibiliPath => Path.Combine(Path.GetDirectoryName(DefaultPath)!, "login-notice-bilibili.json");

    internal bool IsAcknowledged()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is < 1 or > 1024) return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("acknowledgedRevision", out var revisionValue) &&
                   revisionValue.TryGetInt32(out int value) && value == revision;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return false; }
    }

    internal void Acknowledge() => NteLaunchPreferences.WriteAtomic(path, writer =>
    {
        writer.WriteNumber("acknowledgedRevision", revision);
        writer.WriteString("acknowledgedUtc", DateTimeOffset.UtcNow);
    });
}
