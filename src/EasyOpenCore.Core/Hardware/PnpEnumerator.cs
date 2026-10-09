using System.Text;
using EasyOpenCore.Core.Hardware.Native;
using EasyOpenCore.Core.Models;
using static EasyOpenCore.Core.Hardware.Native.SetupApi;

namespace EasyOpenCore.Core.Hardware;

/// <summary>Enumerates every present device through SetupAPI.</summary>
public static class PnpEnumerator
{
    public static List<PnpDevice> EnumeratePresent()
    {
        var result = new List<PnpDevice>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == InvalidHandle)
            throw new System.ComponentModel.Win32Exception();

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var device = new PnpDevice
                {
                    InstanceId = GetString(set, ref data, SetupApi.InstanceId),
                    Name = FirstNonEmpty(
                        GetString(set, ref data, FriendlyName),
                        GetString(set, ref data, DeviceDesc),
                        GetString(set, ref data, BusReportedDesc)),
                    Class = GetString(set, ref data, SetupApi.Class),
                    Manufacturer = GetString(set, ref data, SetupApi.Manufacturer),
                    Service = GetString(set, ref data, SetupApi.Service),
                    DriverVersion = GetString(set, ref data, SetupApi.DriverVersion),
                    DriverKey = GetString(set, ref data, SetupApi.Driver),
                    HardwareIds = GetStringList(set, ref data, SetupApi.HardwareIds),
                    CompatibleIds = GetStringList(set, ref data, SetupApi.CompatibleIds),
                };

                foreach (var path in GetStringList(set, ref data, LocationPaths))
                {
                    if (path.StartsWith("PCIROOT(", StringComparison.OrdinalIgnoreCase) && device.PciPath.Length == 0)
                        device.PciPath = DevicePaths.ToOpenCorePciPath(path);
                    else if (path.StartsWith("ACPI(", StringComparison.OrdinalIgnoreCase) && device.AcpiPath.Length == 0)
                        device.AcpiPath = DevicePaths.ToAcpiPath(path);
                }

                result.Add(device);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static byte[]? GetRaw(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key, out uint type)
    {
        SetupDiGetDevicePropertyW(set, ref data, ref key, out type, null, 0, out var size, 0);
        if (size == 0)
            return null;

        var buffer = new byte[size];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buffer, size, out _, 0) ? buffer : null;
    }

    private static string GetString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = GetRaw(set, ref data, key, out var type);
        if (raw is null || type != DEVPROP_TYPE_STRING)
            return "";
        return Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    private static List<string> GetStringList(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = GetRaw(set, ref data, key, out var type);
        if (raw is null || type != DEVPROP_TYPE_STRING_LIST)
            return [];
        return Encoding.Unicode.GetString(raw)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }
}
