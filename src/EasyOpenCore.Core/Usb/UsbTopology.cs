using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using EasyOpenCore.Core.Hardware;
using EasyOpenCore.Core.Hardware.Native;
using Microsoft.Win32.SafeHandles;

namespace EasyOpenCore.Core.Usb;

public enum UsbPortProtocol { Unknown, Usb2, Usb3 }

/// <summary>A root-hub port as Windows reports it. <see cref="Index"/> is the xHCI port number macOS uses.</summary>
public sealed class UsbPortInfo
{
    public int Index { get; init; }
    public UsbPortProtocol Protocol { get; init; }
    public bool UserConnectable { get; init; } = true;
    public bool TypeC { get; init; }
    public int CompanionPort { get; init; }
    public string? DeviceName { get; init; }
    public bool Connected => DeviceName is not null;
}

public sealed class UsbControllerInfo
{
    public string InstanceId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>True for xHCI (USB 3) controllers.</summary>
    public bool IsXhci { get; init; }
    public string AcpiPath { get; init; } = "";
    public string PciPath { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    /// <summary>"bus:device:function" (decimal), as macOS shows in the "pcidebug" property.</summary>
    public string Bdf { get; init; } = "";
    public List<UsbPortInfo> Ports { get; init; } = [];
}

/// <summary>
/// Reads USB host controllers and their root-hub ports through the Windows USB hub IOCTLs
/// (the same data USBToolBox's usbdump collects).
/// </summary>
public static class UsbTopology
{
    private static readonly Guid HostControllerInterface = new("3ABF6F2D-71C4-462A-8A92-1E6861E6AF27");

    // CTL_CODE(FILE_DEVICE_USB, function, METHOD_BUFFERED, FILE_ANY_ACCESS) from usbioctl.h
    private const uint IoctlGetRootHubName = 0x220408;              // HCD_GET_ROOT_HUB_NAME (258)
    private const uint IoctlGetNodeInformation = 0x220408;          // USB_GET_NODE_INFORMATION (258)
    private const uint IoctlGetConnectionInfoEx = 0x220448;         // 274
    private const uint IoctlGetConnectionDriverKey = 0x220420;      // 264
    private const uint IoctlGetHubInformationEx = 0x220454;         // 277
    private const uint IoctlGetPortConnectorProperties = 0x220458;  // 278
    private const uint IoctlGetConnectionInfoExV2 = 0x22045C;       // 279

    public static List<UsbControllerInfo> Read(IReadOnlyList<Models.PnpDevice>? pnp = null)
    {
        pnp ??= PnpEnumerator.EnumeratePresent();
        var byDriverKey = pnp.Where(d => d.DriverKey.Length > 0)
            .GroupBy(d => d.DriverKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byInstance = pnp.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);

        var result = new List<UsbControllerInfo>();
        foreach (var (devicePath, instanceId, locationInfo) in HostControllers())
        {
            byInstance.TryGetValue(instanceId, out var device);
            string? hubName;
            try { hubName = RootHubName(devicePath); }
            catch (Win32Exception) { continue; }
            if (hubName is null)
                continue;

            var compat = device?.CompatibleIds ?? [];
            result.Add(new UsbControllerInfo
            {
                InstanceId = instanceId,
                Name = device?.Name ?? instanceId,
                IsXhci = HardwareIds.ClassCode(compat) == "0C0330"
                         || (device?.Service.Equals("USBXHCI", StringComparison.OrdinalIgnoreCase) ?? false),
                AcpiPath = device?.AcpiPath ?? "",
                PciPath = device?.PciPath ?? "",
                VendorId = HardwareIds.Vendor(device?.HardwareIds ?? []),
                DeviceId = HardwareIds.Device(device?.HardwareIds ?? []),
                Bdf = ParseBdf(locationInfo),
                Ports = ReadPorts(@"\\.\" + hubName, byDriverKey),
            });
        }
        return result;
    }

    /// <summary>"PCI bus 0, device 20, function 0" → "0:20:0". The text is localized, so only the numbers are used.</summary>
    private static string ParseBdf(string locationInfo)
    {
        var m = System.Text.RegularExpressions.Regex.Match(locationInfo, @"(\d+)\D+(\d+)\D+(\d+)");
        return m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}:{m.Groups[3].Value}" : "";
    }

    private static List<UsbPortInfo> ReadPorts(string hubPath, Dictionary<string, Models.PnpDevice> byDriverKey)
    {
        using var hub = Open(hubPath);
        int portCount = HighestPortNumber(hub);
        var ports = new List<UsbPortInfo>();
        for (int i = 1; i <= portCount; i++)
        {
            var (protocols, _) = ConnectionInfoV2(hub, i);
            var (userConnectable, typeC, companion) = ConnectorProperties(hub, i);
            string? deviceName = null;
            if (IsConnected(hub, i))
            {
                var key = DriverKeyName(hub, i);
                deviceName = key is not null && byDriverKey.TryGetValue(key, out var d) ? d.Name : "USB device";
            }

            ports.Add(new UsbPortInfo
            {
                Index = i,
                Protocol = (protocols & 0b100) != 0 ? UsbPortProtocol.Usb3 : (protocols & 0b010) != 0 ? UsbPortProtocol.Usb2 : UsbPortProtocol.Unknown,
                UserConnectable = userConnectable,
                TypeC = typeC,
                CompanionPort = companion,
                DeviceName = deviceName,
            });
        }
        return ports;
    }

    // ---------- IOCTL helpers ----------

    private static int HighestPortNumber(SafeFileHandle hub)
    {
        // USB_HUB_INFORMATION_EX: HubType (ULONG), HighestPortNumber (USHORT) — counts USB 2 and USB 3 ports of xHCI root hubs.
        var buffer = new byte[512];
        if (Ioctl(hub, IoctlGetHubInformationEx, buffer, out _))
            return BitConverter.ToUInt16(buffer, 4);

        // USB_NODE_INFORMATION (pack 1): NodeType (ULONG), USB_HUB_DESCRIPTOR { bLength, bType, bNumberOfPorts ... }
        Array.Clear(buffer);
        return Ioctl(hub, IoctlGetNodeInformation, buffer, out _) ? buffer[6] : 0;
    }

    private static bool IsConnected(SafeFileHandle hub, int port)
    {
        // USB_NODE_CONNECTION_INFORMATION_EX (pack 1): ConnectionStatus at offset 31; 1 = DeviceConnected.
        var buffer = new byte[512];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        return Ioctl(hub, IoctlGetConnectionInfoEx, buffer, out _) && BitConverter.ToInt32(buffer, 31) == 1;
    }

    private static (uint Protocols, uint Flags) ConnectionInfoV2(SafeFileHandle hub, int port)
    {
        // USB_NODE_CONNECTION_INFORMATION_EX_V2: ConnectionIndex, Length, SupportedUsbProtocols, Flags.
        var buffer = new byte[16];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        BitConverter.GetBytes(16u).CopyTo(buffer, 4);
        BitConverter.GetBytes(0b111u).CopyTo(buffer, 8); // protocols this caller understands: 1.1, 2.0, 3.0
        return Ioctl(hub, IoctlGetConnectionInfoExV2, buffer, out _)
            ? (BitConverter.ToUInt32(buffer, 8), BitConverter.ToUInt32(buffer, 12))
            : (0u, 0u);
    }

    private static (bool UserConnectable, bool TypeC, int Companion) ConnectorProperties(SafeFileHandle hub, int port)
    {
        // USB_PORT_CONNECTOR_PROPERTIES: ConnectionIndex, ActualLength, UsbPortProperties (bit0 user-connectable,
        // bit3 Type-C), CompanionIndex (USHORT), CompanionPortNumber (USHORT), CompanionHubSymbolicLinkName.
        var buffer = new byte[1024];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        if (!Ioctl(hub, IoctlGetPortConnectorProperties, buffer, out _))
            return (true, false, 0);
        uint props = BitConverter.ToUInt32(buffer, 8);
        return ((props & 1) != 0, (props & 8) != 0, BitConverter.ToUInt16(buffer, 14));
    }

    private static string? DriverKeyName(SafeFileHandle hub, int port)
    {
        // USB_NODE_CONNECTION_DRIVERKEY_NAME: ConnectionIndex, ActualLength, DriverKeyName (WCHAR[]).
        var buffer = new byte[1024];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        if (!Ioctl(hub, IoctlGetConnectionDriverKey, buffer, out _))
            return null;
        int length = Math.Min(BitConverter.ToInt32(buffer, 4), buffer.Length) - 8;
        return length > 0 ? Encoding.Unicode.GetString(buffer, 8, length).TrimEnd('\0') : null;
    }

    private static string? RootHubName(string controllerPath)
    {
        using var controller = Open(controllerPath);
        // USB_ROOT_HUB_NAME: ActualLength (ULONG), RootHubName (WCHAR[]).
        var buffer = new byte[1024];
        if (!Ioctl(controller, IoctlGetRootHubName, buffer, out _))
            return null;
        int length = Math.Min(BitConverter.ToInt32(buffer, 0), buffer.Length) - 4;
        return length > 0 ? Encoding.Unicode.GetString(buffer, 4, length).TrimEnd('\0') : null;
    }

    private static SafeFileHandle Open(string path)
    {
        const uint GenericWrite = 0x40000000, FileShareWrite = 0x2, OpenExisting = 3;
        var handle = CreateFileW(path, GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception();
        return handle;
    }

    private static bool Ioctl(SafeFileHandle handle, uint code, byte[] buffer, out int returned) =>
        DeviceIoControl(handle, code, buffer, buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero);

    /// <summary>Device paths of USB host controller interfaces, with their instance IDs and location info.</summary>
    private static IEnumerable<(string Path, string InstanceId, string LocationInfo)> HostControllers()
    {
        var guid = HostControllerInterface;
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == SetupApi.InvalidHandle)
            yield break;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                var info = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out uint size, ref info);
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, size, out _, ref info))
                        continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    yield return (path, Property(set, ref info, SetupApi.InstanceId), Property(set, ref info, LocationInfo));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static readonly SetupApi.DEVPROPKEY LocationInfo = new("a45c254e-df1c-4efd-8020-67d146a850e0", 15);

    private static string Property(IntPtr set, ref SetupApi.SP_DEVINFO_DATA info, SetupApi.DEVPROPKEY key)
    {
        SetupApi.SetupDiGetDevicePropertyW(set, ref info, ref key, out _, null, 0, out uint size, 0);
        if (size == 0)
            return "";
        var buffer = new byte[size];
        return SetupApi.SetupDiGetDevicePropertyW(set, ref info, ref key, out _, buffer, size, out _, 0)
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : "";
    }

    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail,
        uint detailSize, out uint requiredSize, ref SetupApi.SP_DEVINFO_DATA info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] inBuffer, int inSize,
        byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
}
