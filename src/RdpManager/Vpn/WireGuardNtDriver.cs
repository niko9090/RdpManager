using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace RdpManager.Vpn;

/// <summary>Un adattatore wireguard-nt aperto (vive finché il processo tiene l'handle).</summary>
public interface INtAdapter : IDisposable
{
    void SetConfiguration(byte[] block);
    void AddAddress(IPAddress ip, int prefix);
    void AddRoute(IPAddress network, int prefix);
    void Up();
    /// <summary>Blocco restituito da WireGuardGetConfiguration, null se non leggibile.</summary>
    byte[]? GetConfiguration();
}

/// <summary>Creazione di adattatori wireguard-nt (la parte nativa, sostituibile da una finta nei test).</summary>
public interface INtDriver
{
    INtAdapter Create(string name, string tunnelType);
}

/// <summary>
/// API di wireguard-nt (wireguard.dll ufficiale, nella cartella del programma) e di iphlpapi per indirizzo e rotte, che
/// wireguard-nt non assegna da solo. Serve l'amministratore (lo fa il servizio, che gira come SYSTEM).
/// </summary>
internal sealed class WireGuardNtDriver : INtDriver
{
    // GUID fisso: l'adattatore ha sempre la stessa identità (e la stessa rete «nota» in Windows).
    private static readonly Guid AdapterGuid = new("5e0c7a1d-52b3-4a43-9c61-7d2f0b8f6a10");

    public INtAdapter Create(string name, string tunnelType)
    {
        var guid = AdapterGuid;
        var handle = WireGuardCreateAdapter(name, tunnelType, ref guid);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WireGuardCreateAdapter non riuscita");
        return new Adapter(handle);
    }

    private sealed class Adapter(IntPtr handle) : INtAdapter
    {
        private IntPtr _handle = handle;

        public void SetConfiguration(byte[] block)
        {
            if (!WireGuardSetConfiguration(_handle, block, (uint)block.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WireGuardSetConfiguration non riuscita");
        }

        public void AddAddress(IPAddress ip, int prefix)
        {
            var row = WireGuardNtStructs.BuildUnicastRow(Luid(), ip, prefix);
            WithPinned(row, p =>
            {
                var rc = CreateUnicastIpAddressEntry(p);
                if (rc != 0 && rc != 5010) // 5010: ERROR_OBJECT_ALREADY_EXISTS
                    throw new Win32Exception((int)rc, "CreateUnicastIpAddressEntry non riuscita");
            });
        }

        public void AddRoute(IPAddress network, int prefix)
        {
            var row = WireGuardNtStructs.BuildForwardRow(Luid(), network, prefix);
            WithPinned(row, p =>
            {
                var rc = CreateIpForwardEntry2(p);
                if (rc != 0 && rc != 5010)
                    throw new Win32Exception((int)rc, "CreateIpForwardEntry2 non riuscita");
            });
        }

        public void Up()
        {
            if (!WireGuardSetAdapterState(_handle, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WireGuardSetAdapterState non riuscita");
        }

        public byte[]? GetConfiguration()
        {
            uint size = 0;
            if (!WireGuardGetConfiguration(_handle, null, ref size) && Marshal.GetLastWin32Error() == 234 && size > 0) // ERROR_MORE_DATA
            {
                var buf = new byte[size];
                if (WireGuardGetConfiguration(_handle, buf, ref size))
                    return buf.AsSpan(0, (int)size).ToArray();
            }
            return null;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                WireGuardCloseAdapter(_handle); // l'adattatore creato da noi sparisce con l'handle
                _handle = IntPtr.Zero;
            }
        }

        private ulong Luid()
        {
            WireGuardGetAdapterLUID(_handle, out var luid);
            return luid;
        }

        private static void WithPinned(byte[] data, Action<IntPtr> action)
        {
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                action(h.AddrOfPinnedObject());
            }
            finally
            {
                h.Free();
            }
        }
    }

    [DllImport("wireguard.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr WireGuardCreateAdapter(string name, string tunnelType, ref Guid requestedGuid);

    [DllImport("wireguard.dll")]
    private static extern void WireGuardCloseAdapter(IntPtr adapter);

    [DllImport("wireguard.dll")]
    private static extern void WireGuardGetAdapterLUID(IntPtr adapter, out ulong luid);

    [DllImport("wireguard.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WireGuardSetAdapterState(IntPtr adapter, int state);

    [DllImport("wireguard.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WireGuardSetConfiguration(IntPtr adapter, byte[] config, uint bytes);

    [DllImport("wireguard.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WireGuardGetConfiguration(IntPtr adapter, byte[]? config, ref uint bytes);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateUnicastIpAddressEntry(IntPtr row);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateIpForwardEntry2(IntPtr row);
}
