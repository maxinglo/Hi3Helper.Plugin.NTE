using Nte.Login;
using System;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal static class NteLoginHostLaunch
{
    internal static int StartSwitch(NteRegionSwitchOptions options)
        => NteDetachedProcess.Start(ResolveHostPath(FindPluginDirectory()), options.ToArguments());

    internal static int Start(NteHostLaunchOptions options)
        => NteDetachedProcess.Start(ResolveHostPath(FindPluginDirectory()), options.ToArguments());

    internal static string ResolveHostPath(string pluginDirectory)
    {
        pluginDirectory = Path.GetFullPath(pluginDirectory);
        string payload = Path.Combine(pluginDirectory, "Nte.PluginPayload.zip");
        if (!File.Exists(payload))
            return VerifyHostPath(Path.Combine(pluginDirectory, "LoginHost"));

        using FileStream archive = File.OpenRead(payload);
        string digest = Convert.ToHexString(SHA256.HashData(archive));
        string extracted = Path.Combine(pluginDirectory, "Nte.PluginPayload-" + digest);
        if (Directory.Exists(extracted))
            return VerifyHostPath(Path.Combine(extracted, "LoginHost"));

        // Release attachments are flat. Restore the LoginHost layout before
        // exposing a complete directory to other launcher processes.
        string staging = extracted + "." + Guid.NewGuid().ToString("N");
        try
        {
            archive.Position = 0;
            ZipFile.ExtractToDirectory(archive, staging);
            VerifyHostPath(Path.Combine(staging, "LoginHost"));
            try
            {
                Directory.Move(staging, extracted);
            }
            catch (IOException) when (Directory.Exists(extracted))
            {
                // Another process already finished extracting the same payload.
            }
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }

        return VerifyHostPath(Path.Combine(extracted, "LoginHost"));
    }

    private static string VerifyHostPath(string root)
    {
        foreach (string file in new[]
                 {
                     "Nte.LoginHost.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll",
                     "Assets/question-mascot.png",
                     "Assets/Brand/google-g.png", "Assets/Brand/facebook-primary.png"
                 })
            if (!File.Exists(Path.Combine(root, file)))
                throw new FileNotFoundException("The bundled NTE login component is incomplete.");
        return Path.Combine(root, "Nte.LoginHost.exe");
    }

    private static unsafe string FindPluginDirectory()
    {
        // AppContext.BaseDirectory belongs to Collapse. Resolve the native DLL
        // containing our own export, so moved/custom plugin folders also work.
        nint anchor = (nint)(delegate* unmanaged[Cdecl]<char*, void**, int>)&Exports.TryGetApiExport;
        if (!GetModuleHandleExW(0x6, anchor, out nint module)) throw new Win32Exception(Marshal.GetLastWin32Error());
        char* buffer = stackalloc char[32768];
        uint length = GetModuleFileNameW(module, buffer, 32768);
        if (length is 0 or >= 32768) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetDirectoryName(new string(buffer, 0, (int)length)) ??
               throw new InvalidDataException("Plugin directory unavailable.");
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [DllImport("kernel32", SetLastError = true)]
    private static extern unsafe uint GetModuleFileNameW(nint module, char* buffer, uint length);
}
