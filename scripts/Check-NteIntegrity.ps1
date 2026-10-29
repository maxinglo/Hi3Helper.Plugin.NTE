[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $GameRoot,
    [string] $PluginPath = $(if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'NTE.dll')) { Join-Path $PSScriptRoot 'NTE.dll' } else { "$env:USERPROFILE\AppData\LocalLow\CollapseLauncher\GameFolder\_plugins\Hi3Helper.Plugin.NTE\NTE.dll" }),
    [string] $ManifestPath,
    [string] $ReportPath = (Join-Path $env:LOCALAPPDATA ('CollapseLauncher/Plugins/NTE/Reports/integrity-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')),
    [switch] $Quiet,
    [switch] $Repair
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $GameRoot).Path.TrimEnd('\')
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$manifest = if ($ManifestPath) { (Resolve-Path -LiteralPath $ManifestPath).Path } else { '' }
$report = [IO.Path]::GetFullPath($ReportPath)
if ($report.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Save the report outside the game directory.' }
if (-not ('NteIntegrityNativeClient' -as [type])) {
    Add-Type -CompilerOptions '/unsafe' -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static unsafe class NteIntegrityNativeClient
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Progress(int phase, long processed, long total, int checkedFiles, int totalFiles, char* path, int pathLength);
    private static readonly Progress Callback = OnProgress;
    private static int cancelled;
    private static bool quiet;
    private static readonly Stopwatch Timer = new Stopwatch();
    private static long lastMilliseconds;

    private static int OnProgress(int phase, long processed, long total, int checkedFiles, int totalFiles, char* path, int pathLength)
    {
        try
        {
            long now = Timer.ElapsedMilliseconds;
            if (!quiet && (now - lastMilliseconds >= 500 || checkedFiles == totalFiles))
            {
                lastMilliseconds = now;
                Console.WriteLine("{0} {1,6:F1}%  {2}/{3}  {4}", phase == 1 ? "Check" : "Repair", total == 0 ? 100 : processed * 100.0 / total,
                    checkedFiles, totalFiles, new string(path, 0, pathLength));
            }
            return Volatile.Read(ref cancelled);
        }
        catch { return 1; } // Never allow a managed exception through a native callback.
    }

    public static int Run(string plugin, string root, string manifest, string report, bool suppressProgress, bool repair)
    {
        // NativeAOT libraries remain loaded for the lifetime of this command process.
        nint module = NativeLibrary.Load(plugin);
        nint resolver = NativeLibrary.GetExport(module, "TryGetApiExport");
        void* entry = null;
        string operationName = repair ? "RepairNteGame" : "CheckNteGameIntegrity";
        fixed (char* name = operationName)
        {
            int hr = ((delegate* unmanaged[Cdecl]<char*, void**, int>)resolver)(name, &entry);
            if (hr != 0 || entry == null) throw new NotSupportedException("This NTE plugin has no integrity-check export. Install the new plugin package first.");
        }
        cancelled = 0;
        quiet = suppressProgress;
        lastMilliseconds = -500;
        Timer.Restart();
        ConsoleCancelEventHandler onCancel = (sender, e) => { e.Cancel = true; Interlocked.Exchange(ref cancelled, 1); };
        Console.CancelKeyPress += onCancel;
        try
        {
            fixed (char* rootP = root)
            fixed (char* manifestP = manifest)
            fixed (char* reportP = report)
                return ((delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint, int>)entry)(
                    rootP, root.Length, manifestP, manifest.Length, reportP, report.Length,
                    Marshal.GetFunctionPointerForDelegate(Callback));
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
'@
}
Write-Host $(if ($Repair) { 'Checking and repairing NTE files. Ctrl+C cancels. Uses the latest official release.' } else { 'Checking NTE files (read only). Ctrl+C cancels. The default manifest is the latest official release.' })
$result = [NteIntegrityNativeClient]::Run($plugin, $root, $manifest, $report, [bool]$Quiet, [bool]$Repair)
if ($result -eq -2147023673) { Write-Host "Cancelled. Partial report: $report"; return }
if ($result -lt 0) { throw [Runtime.InteropServices.Marshal]::GetExceptionForHR($result) }
$summary = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
Write-Host "Checked $($summary.CheckedFiles)/$($summary.TotalFiles) files; issues=$($summary.Issues.Count); healthy=$($summary.Healthy)"
Write-Host "Report: $report"
$summary
