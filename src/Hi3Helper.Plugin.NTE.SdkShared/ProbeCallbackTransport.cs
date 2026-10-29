using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Uses the documented Windows COPYDATA transport, never remote execution or
// an injected hook. Registration must originate from the owned pipe client.
internal sealed class ProbeCallbackTransport
{
    private readonly nint target, source;
    private readonly int clientId;

    private ProbeCallbackTransport(nint target, nint source, int clientId)
    {
        this.target = target; this.source = source; this.clientId = clientId;
        ValidateWindows();
    }

    internal static ProbeCallbackTransport FromRegistration(ReadOnlySpan<byte> frame, int clientId, nint source, string expectedApplication = "1289")
    {
        if (clientId <= 0 || frame.Length != 1244 || BinaryPrimitives.ReadUInt32LittleEndian(frame) != 0x6B647331 ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(frame[8..]) != frame.Length)
            throw new InvalidDataException("Expected the owned client's registration frame.");
        byte[] body = frame[12..].ToArray();
        try
        {
            for (int i = 0; i < body.Length; i++) body[i] ^= unchecked((byte)clientId);
            // HWND is a four-byte protocol field even in this x64 bridge.
            nint window = (nint)BinaryPrimitives.ReadUInt32LittleEndian(body);
            string app = System.Text.Encoding.Unicode.GetString(body, 4, 256).Split('\0')[0];
            string version = System.Text.Encoding.Unicode.GetString(body, 0x40C, 64).Split('\0')[0];
            if (expectedApplication is not ("1289" or "global") || app != expectedApplication || version != "2.0.8.0") throw new InvalidDataException("Unverified application or bridge version.");
            return new ProbeCallbackTransport(window, source, clientId);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    internal void Send(ProbeLoginSequence sequence, CancellationToken token)
    {
        byte[][] packets = sequence.CopyEncodedPackets();
        SendPackets(packets, token);
    }

    internal void Send(ProbePreLoginResponse response, ProbePreLoginRequest request, CancellationToken token) =>
        SendPackets([response.Encode(request, Environment.ProcessId)], token);

    internal void SendGlobal(Nte.Login.NteGlobalIdentity identity, CancellationToken token) =>
        SendPackets(Nte.Login.NteGlobalLoginPacket.EncodeSequence(identity, Environment.ProcessId), token);

    internal void SendGlobal(Nte.Login.NteGlobalSdkQuery request, System.Text.Json.JsonElement response, CancellationToken token) =>
        SendPackets([Nte.Login.NteGlobalPacket.EncodeQuery(request, response, Environment.ProcessId)], token);

    private void SendPackets(byte[][] packets, CancellationToken token)
    {
        try
        {
            foreach (byte[] packet in packets)
            {
                token.ThrowIfCancellationRequested();
                ValidateWindows(); // HWNDs can be recycled after a client exits.
                GCHandle pinned = GCHandle.Alloc(packet, GCHandleType.Pinned);
                try
                {
                    CopyData data = new() { Tag = 0x70676D77, Length = packet.Length, Data = pinned.AddrOfPinnedObject() };
                    // A nonzero return only means the message was delivered. The
                    // receiving window's LRESULT is not proof of account login.
                    if (SendMessageTimeoutW(target, 0x4A, (nuint)source, ref data, 0x23, 5000, out _) == 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SDK callback delivery did not complete.");
                }
                finally { pinned.Free(); }
            }
        }
        finally { foreach (byte[] packet in packets) CryptographicOperations.ZeroMemory(packet); }
    }

    private void ValidateWindows()
    {
        if (source == 0 || target == 0 || GetWindowThreadProcessId(source, out uint owner) == 0 || owner != Environment.ProcessId ||
            GetWindowThreadProcessId(target, out uint peer) == 0 || peer != clientId)
            throw new InvalidOperationException("SDK callback windows do not belong to this host and its owned client.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public nuint Tag; public int Length; public nint Data; }
    [DllImport("user32", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, ref CopyData data, uint flags, uint timeout, out nuint result);
}
