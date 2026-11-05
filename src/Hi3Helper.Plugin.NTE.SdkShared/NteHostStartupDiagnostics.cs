using System.ComponentModel;
using System.Text.Json;

namespace Nte.Login;

internal static class NteHostStartupDiagnostics
{
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Collapse", "NTE", "Diagnostics", "last-startup.json");

    // Exception messages, stack traces, arguments and SDK frames can contain credentials.
    // Persist only fixed failure identifiers and numeric native status.
    internal static void TryWrite(string path, Exception error, string key)
    {
        try
        {
            if (key.Length > 64 || key.Any(c => !char.IsAsciiLetterOrDigit(c))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow,
                hostVersion = typeof(NteHostStartupDiagnostics).Assembly.GetName().Version?.ToString(),
                key, exceptionType = error.GetType().Name,
                nativeErrorCode = error is Win32Exception native ? native.NativeErrorCode : (int?)null },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
