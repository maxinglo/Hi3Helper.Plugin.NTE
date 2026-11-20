using System.Runtime.InteropServices;
using Nte.Login;
using Nte.LoginHost.Localization;

namespace Nte.LoginHost;

internal static partial class OfficialLauncherShortcutPromptWriter
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterface = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileInterface = new("0000010B-0000-0000-C000-000000000046");

    internal static void Create(NteOfficialLaunchPlan plan, NteHostRegion region, Environment.SpecialFolder folder)
    {
        if (folder is not (Environment.SpecialFolder.Programs or Environment.SpecialFolder.DesktopDirectory))
            throw new ArgumentOutOfRangeException(nameof(folder));
        string directory = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        CreateInDirectory(plan, region, directory);
    }

    internal static void CreateInDirectory(NteOfficialLaunchPlan plan, NteHostRegion region, string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new DirectoryNotFoundException("Shortcut folder is unavailable.");
        string executable = plan.Executable;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The installed official launcher is unavailable.", executable);
        string name = LoginText.Get(region switch
        {
            NteHostRegion.CN => "OfficialShortcutNameMainland",
            NteHostRegion.Bilibili => "OfficialShortcutNameBilibili",
            NteHostRegion.Global => "OfficialShortcutNameGlobal",
            NteHostRegion.Taiwan => "OfficialShortcutNameTaiwan",
            _ => throw new ArgumentOutOfRangeException(nameof(region))
        });
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".nte-{Guid.NewGuid():N}.lnk");
        try
        {
            SaveShellLink(temporary, executable, Path.GetDirectoryName(executable)!);
            string destination = Path.Combine(directory, name + ".lnk");
            for (int suffix = 2; ; suffix++)
            {
                try
                {
                    File.Move(temporary, destination);
                    break;
                }
                catch (IOException error) when (error.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7))
                {
                    destination = Path.Combine(directory, FormattableString.Invariant($"{name} ({suffix}).lnk"));
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void SaveShellLink(string destination, string executable, string workingDirectory)
    {
        const uint apartmentThreaded = 2;
        CheckResult(CoInitializeEx(0, apartmentThreaded));
        try
        {
            SaveInitializedShellLink(destination, executable, workingDirectory);
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static unsafe void SaveInitializedShellLink(string destination, string executable, string workingDirectory)
    {
        // Call the Windows interfaces directly so the published host does not need built-in COM interop.
        const uint inProcessServer = 1;
        CheckResult(CoCreateInstance(ShellLinkClass, 0, inProcessServer, ShellLinkInterface,
            out nint shellLink));
        try
        {
            nint* linkMethods = *(nint**)shellLink;
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)linkMethods[0];
            // IShellLinkW methods in shobjidl_core.h order, including the three IUnknown methods.
            var setWorkingDirectory = (delegate* unmanaged[Stdcall]<nint, char*, int>)linkMethods[9];
            var setPath = (delegate* unmanaged[Stdcall]<nint, char*, int>)linkMethods[20];
            fixed (char* path = executable, directory = workingDirectory)
            {
                CheckResult(setPath(shellLink, path));
                CheckResult(setWorkingDirectory(shellLink, directory));
            }

            nint persistFile;
            Guid iid = PersistFileInterface;
            CheckResult(queryInterface(shellLink, &iid, &persistFile));
            try
            {
                // IPersistFile::Save follows IUnknown, IPersist::GetClassID, IsDirty and Load.
                var saveFile = (delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)persistFile)[6];
                fixed (char* path = destination)
                    CheckResult(saveFile(persistFile, path, 0));
            }
            finally
            {
                Release(persistFile);
            }
        }
        finally
        {
            Release(shellLink);
        }
    }

    private static unsafe void Release(nint instance)
        => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);

    private static void CheckResult(int result)
    {
        if (result < 0) throw new COMException("The Windows Shell could not save the shortcut.", result);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
