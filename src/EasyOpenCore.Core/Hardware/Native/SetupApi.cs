using System.Runtime.InteropServices;

namespace EasyOpenCore.Core.Hardware.Native;

internal static class SetupApi
{
    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_ALLCLASSES = 0x4;

    public const uint DEVPROP_TYPE_UINT32 = 0x07;
    public const uint DEVPROP_TYPE_STRING = 0x12;
    public const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    public static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(string guid, uint pid)
        {
            fmtid = new Guid(guid);
            this.pid = pid;
        }
    }

    private const string DeviceGuid = "a45c254e-df1c-4efd-8020-67d146a850e0";

    public static readonly DEVPROPKEY DeviceDesc = new(DeviceGuid, 2);
    public static readonly DEVPROPKEY HardwareIds = new(DeviceGuid, 3);
    public static readonly DEVPROPKEY CompatibleIds = new(DeviceGuid, 4);
    public static readonly DEVPROPKEY Service = new(DeviceGuid, 6);
    public static readonly DEVPROPKEY Class = new(DeviceGuid, 9);
    public static readonly DEVPROPKEY Driver = new(DeviceGuid, 11);
    public static readonly DEVPROPKEY Manufacturer = new(DeviceGuid, 13);
    public static readonly DEVPROPKEY FriendlyName = new(DeviceGuid, 14);
    public static readonly DEVPROPKEY LocationPaths = new(DeviceGuid, 37);
    public static readonly DEVPROPKEY InstanceId = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    public static readonly DEVPROPKEY BusReportedDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    public static readonly DEVPROPKEY DriverVersion = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 3);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

internal static class Kernel32
{
    public const uint ProviderAcpi = 0x41435049; // 'ACPI'

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint EnumSystemFirmwareTables(uint firmwareTableProviderSignature, byte[]? buffer, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId, byte[]? buffer, uint bufferSize);

    /// <summary>1 = BIOS, 2 = UEFI.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFirmwareType(out uint firmwareType);
}
