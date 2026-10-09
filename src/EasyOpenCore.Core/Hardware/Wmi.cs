using System.Management;

namespace EasyOpenCore.Core.Hardware;

internal static class Wmi
{
    public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    public static string Str(ManagementBaseObject o, string prop) =>
        (Get(o, prop)?.ToString() ?? "").Trim();

    public static int Int(ManagementBaseObject o, string prop) =>
        Get(o, prop) is { } v ? Convert.ToInt32(v) : 0;

    public static ulong ULong(ManagementBaseObject o, string prop) =>
        Get(o, prop) is { } v ? Convert.ToUInt64(v) : 0;

    public static object? Get(ManagementBaseObject o, string prop)
    {
        try { return o[prop]; }
        catch (ManagementException) { return null; }
    }
}
