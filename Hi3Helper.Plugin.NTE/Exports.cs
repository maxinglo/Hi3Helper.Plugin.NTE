using Hi3Helper.Plugin.Core;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hi3Helper.Plugin.NTE;

public partial class Exports : SharedStaticV1Ext<Exports>
{
    static Exports() => Load<NtePlugin>(!RuntimeFeature.IsDynamicCodeCompiled ? new Core.Management.GameVersion(0, 1, 0, 0) : default);

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* exportName, void** delegateP) =>
        TryGetApiExportPointer(exportName, delegateP);
}

