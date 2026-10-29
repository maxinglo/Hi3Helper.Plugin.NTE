using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nte.LoginHost;

internal static class BrowserCallbackPipe
{
    internal static NamedPipeServerStream Create(string name)
    {
        // CurrentUserOnly compares elevation levels on Windows. Browser
        // callbacks run unelevated while the game host may be elevated. Grant
        // this user's SID explicitly instead of granting an elevation group.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("Windows user unavailable.");
        PipeSecurity security = new(); security.SetAccessRuleProtection(true, false); security.SetOwner(user);
        security.AddAccessRule(new(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 0, security);
    }
    internal static bool IsOwnedByCurrentUser(NamedPipeClientStream pipe)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User is SecurityIdentifier user &&
               pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner.Equals(user);
    }
}
