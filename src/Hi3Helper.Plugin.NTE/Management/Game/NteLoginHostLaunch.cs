using Nte.Login;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal static class NteLoginHostLaunch
{
    internal static int StartSwitch(NteRegionSwitchOptions options)
        => NteDetachedProcess.Start(ResolveHostPath(FindPluginDirectory()), options.ToArguments());

    internal static int Start(NteHostLaunchOptions options)
        => NteDetachedProcess.Start(ResolveHostPath(FindPluginDirectory()), options.ToArguments());

    internal static string ResolveHostPath(string pluginDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(pluginDirectory), "LoginHost");
        foreach (string file in new[] { "Nte.LoginHost.exe", "Nte.LoginHost.dll", "Nte.LoginHost.runtimeconfig.json", "Assets/question-mascot.png" })
            if (!File.Exists(Path.Combine(root, file))) throw new FileNotFoundException("The bundled NTE login component is incomplete.");
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
        return Path.GetDirectoryName(new string(buffer, 0, (int)length)) ?? throw new InvalidDataException("Plugin directory unavailable.");
    }

    [DllImport("kernel32", SetLastError = true)] private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);
    [DllImport("kernel32", SetLastError = true)] private static extern unsafe uint GetModuleFileNameW(nint module, char* buffer, uint length);
}
