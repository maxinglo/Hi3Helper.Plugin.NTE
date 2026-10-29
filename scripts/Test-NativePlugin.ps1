[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path,
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$pluginPath = (Resolve-Path -LiteralPath $Path).Path
if (-not ('NteNativeSmoke' -as [type])) {
    Add-Type -CompilerOptions '/unsafe' -TypeDefinition @'
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;

public static unsafe class NteNativeSmoke
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Logger(int* level, void* eventId, char* message, int length);
    private static readonly Logger Log = (level, eventId, message, length) =>
    {
        if (*level >= 3) Console.WriteLine(new string(message, 0, length));
    };
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct MemoryBlock
    {
        public byte* Handle;
        public int Length;
        public byte IsDisposable;
    }

    public static string Run(string path, string expectedVersion)
    {
        // NativeAOT libraries cannot be unloaded safely; keep the module loaded for this process.
        nint module = NativeLibrary.Load(path);
        nint resolver = NativeLibrary.GetExport(module, "TryGetApiExport");
        nint Find(string name)
        {
            void* result = null;
            fixed (char* key = name)
            {
                int status = ((delegate* unmanaged[Cdecl]<char*, void**, int>)resolver)(key, &result);
                if (status != 0 || result == null)
                    throw new InvalidOperationException("Missing plugin API export: " + name);
            }
            return (nint)result;
        }
        Version ReadVersion(string export)
        {
            int* data = (int*)((delegate* unmanaged[Cdecl]<void*>)Find(export))();
            return new Version(data[0], data[1], data[2], data[3]);
        }
        Version standard = ReadVersion("GetPluginStandardVersion");
        if (standard != new Version(0, 1, 6, 0))
            throw new InvalidOperationException("Unexpected API standard: " + standard);
        Version version = ReadVersion("GetPluginVersion");
        ((delegate* unmanaged[Cdecl]<nint, void>)Find("SetLoggerCallback"))(Marshal.GetFunctionPointerForDelegate(Log));
        if (!string.IsNullOrEmpty(expectedVersion))
        {
            Version expected = Version.Parse(expectedVersion);
            expected = new Version(expected.Major, expected.Minor, expected.Build, Math.Max(0, expected.Revision));
            if (version != expected)
                throw new InvalidOperationException("Plugin version mismatch: " + version + " != " + expected);
        }
        foreach (string export in new[] { "GetPlugin", "GetGameSettingsPage", "SetGameSettingValue", "ApplyGameSettings", "CheckNteGameIntegrity", "RepairNteGame", "PlanNteRegionSwitch", "SwitchNteRegion" })
            Find(export);
        // Exercise the NativeAOT JSON/result ABI without downloading or touching
        // a real game installation. Recovery preview must be strictly read-only.
        string fixture = Path.Combine(Path.GetDirectoryName(path), ".region-switch-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, ".collapse-nte"));
        try
        {
            string journal = Path.Combine(fixture, ".collapse-nte", "switch.json");
            File.WriteAllText(journal, "{}");
            string report = Path.Combine(fixture, "result.json");
            string target = "2000013";
            int status;
            fixed (char* rootPointer = fixture, targetPointer = target, reportPointer = report)
                status = ((delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint, int>)Find("PlanNteRegionSwitch"))(
                    rootPointer, fixture.Length, targetPointer, target.Length, reportPointer, report.Length, 0);
            Check(status, "PlanNteRegionSwitch");
            if (!File.ReadAllText(report).Contains("\"RecoveryRequired\": true") || File.ReadAllText(journal) != "{}")
                throw new InvalidOperationException("Native region preview failed to preserve a pending transaction.");
            File.Delete(journal);
            target = "1289";
            fixed (char* rootPointer = fixture, targetPointer = target, reportPointer = report)
                status = ((delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint, int>)Find("SwitchNteRegion"))(
                    rootPointer, fixture.Length, targetPointer, target.Length, reportPointer, report.Length, 0);
            if (status >= 0 || !File.ReadAllText(report).Contains("Taiwan and Global only"))
                throw new InvalidOperationException("Native switch did not reject an unsupported region.");
        }
        finally { Directory.Delete(fixture, true); }
        nint unknown = (nint)((delegate* unmanaged[Cdecl]<void*>)Find("GetPlugin"))();
        if (unknown == 0)
            throw new InvalidOperationException("GetPlugin returned a null COM pointer.");
        nint plugin = 0;
        try
        {
            Guid pluginId = new Guid("436f6c6c-6170-7365-727f-000000000010");
            void** unknownTable = *(void***)unknown;
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)unknownTable[0])(unknown, &pluginId, &plugin));
            // IPlugin's vtable is fixed by the pinned Core API 0.1.6 contract.
            void** table = *(void***)plugin;
            int count = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, int*, int>)table[8])(plugin, &count));
            if (count != 3)
                throw new InvalidOperationException("Expected separate China, Global and Taiwan NTE presets.");
            foreach (string locale in new[] { "en-US", "zh-CN" })
            {
                fixed (char* localePointer = locale)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, char*, int>)table[14])(plugin, localePointer));
                char* name = null;
                try
                {
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, char**, int>)table[4])(plugin, &name));
                    string expectedName = locale == "zh-CN" ? "\u5f02\u73af\u63d2\u4ef6" : "Neverness to Everness Plugin";
                    if (Marshal.PtrToStringUni((nint)name) != expectedName)
                        throw new InvalidOperationException("Localized plugin metadata failed for " + locale);
                }
                finally { Marshal.FreeCoTaskMem((nint)name); }
            }
            for (int presetIndex = 0; presetIndex < count; presetIndex++)
            {
            nint preset = 0;
            Check(((delegate* unmanaged[MemberFunction]<nint, int, nint*, int>)table[11])(plugin, presetIndex, &preset),
                "IPlugin.GetPresetConfig");
            if (preset == 0)
                throw new InvalidOperationException("Missing NTE preset COM pointer.");
            try
            {
                MemoryBlock page = default;
                int result = ((delegate* unmanaged[Cdecl]<nint, MemoryBlock*, int>)Find("GetGameSettingsPage"))(preset, &page);
                Check(result, "GetGameSettingsPage");
                try
                {
                    if (result != 0 || page.Handle == null || page.Length <= 0)
                        throw new InvalidOperationException("The NTE appearance settings page was not returned.");
                    string json = Marshal.PtrToStringUTF8((nint)page.Handle, page.Length);
                    if (json == null || !json.Contains("dynamicBackground") || !json.Contains("overlayEnabled") ||
                        !json.Contains("launch.graphicsApi") || !json.Contains("dx11") ||
                        !json.Contains("login.experimentalWindowsHello") ||
                        (presetIndex > 0 && !json.Contains("server.switchTarget")))
                        throw new InvalidOperationException("The native appearance settings page is incomplete.");
                }
                finally { if (page.IsDisposable != 0) NativeMemory.Free(page.Handle); }
            }
            finally { Marshal.Release(preset); }
            }
        }
        finally
        {
            if (plugin != 0) Marshal.Release(plugin);
            Marshal.Release(unknown);
        }
        return "Native ABI, region preview/rejection, en/zh metadata and appearance UI OK; Core " + standard + "; NTE " + version;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException(operation + " failed with HRESULT 0x" + result.ToString("X8"),
                Marshal.GetExceptionForHR(result));
    }
}
'@
}
Write-Host ([NteNativeSmoke]::Run($pluginPath, $ExpectedVersion))
