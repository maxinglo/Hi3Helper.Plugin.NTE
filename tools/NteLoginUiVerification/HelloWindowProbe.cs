using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

// Exercises our owner's z-order with an explicitly labelled, empty owned
// window. This never opens, locates, or controls a Windows security dialog.
internal static class HelloWindowProbe
{
    internal static object Run(Assembly host, Window owner, JsonElement request)
    {
        string outcome = request.TryGetProperty("outcome", out JsonElement mode) ? mode.GetString()! : "success";
        if (outcome is not ("success" or "cancel" or "failure")) throw new InvalidDataException("probe-outcome");
        bool initiallyTopmost =
            request.TryGetProperty("initiallyTopmost", out JsonElement initial) && initial.GetBoolean();
        nint hwnd = new WindowInteropHelper(owner).Handle;
        bool actualInitial = Topmost(hwnd);
        bool promoted = false, childPromoted = false, owned = false, restored;
        string observed = "success";
        Window? child = null;
        SetTopmost(hwnd, initiallyTopmost);
        try
        {
            try
            {
                using IDisposable scope = (IDisposable)Activator.CreateInstance(
                    host.GetType("Nte.LoginHost.Accounts.HelloPromptWindowScope", true)!, UiSession.Members,
                    null, [hwnd, false], null)!;
                promoted = Topmost(hwnd);
                child = new Window
                {
                    Title = "Owned window fixture - not Windows Hello", Owner = owner,
                    ShowActivated = false, ShowInTaskbar = false, Width = 160, Height = 80,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000
                };
                child.Show();
                nint childHandle = new WindowInteropHelper(child).Handle;
                childPromoted = Topmost(childHandle);
                owned = GetWindow(childHandle, 4) == hwnd;
                if (outcome == "cancel") throw new OperationCanceledException("Synthetic cancellation.");
                if (outcome == "failure") throw new IOException("Synthetic failure.");
            }
            catch (OperationCanceledException)
            {
                observed = "cancel";
            }
            catch (IOException)
            {
                observed = "failure";
            }
            finally
            {
                child?.Close();
            }

            restored = Topmost(hwnd) == initiallyTopmost;
        }
        finally
        {
            SetTopmost(hwnd, actualInitial);
        }

        return new
        {
            outcome, observed, initiallyTopmost, ownerPromoted = promoted, childPromoted, owned,
            ownerRestored = restored, originalStateRestored = Topmost(hwnd) == actualInitial,
            systemDialogUsed = false, securityGestureRequested = false
        };
    }

    private static bool Topmost(nint hwnd) => (GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0;

    private static void SetTopmost(nint hwnd, bool enabled)
    {
        if (!SetWindowPos(hwnd, enabled ? -1 : -2, 0, 0, 0, 0, 0x213))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
}
