using System.Text;
using EasyOpenCore.Core.Hardware.Native;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Hardware;

/// <summary>
/// Reads the firmware ACPI tables (DSDT, SSDTs, FACP...) for analysis and SSDT generation.
/// Windows API limitation: when several tables share a signature (e.g. SSDT),
/// GetSystemFirmwareTable only returns the first one.
/// </summary>
public static class AcpiTableReader
{
    public static List<AcpiTable> ReadAll()
    {
        var tables = new List<AcpiTable>();
        uint size = Kernel32.EnumSystemFirmwareTables(Kernel32.ProviderAcpi, null, 0);
        if (size == 0)
            return tables;

        var ids = new byte[size];
        Kernel32.EnumSystemFirmwareTables(Kernel32.ProviderAcpi, ids, size);

        // The DSDT is not listed by the enumeration, but it can be read directly by its ID.
        var tableIds = new List<uint> { SignatureToId("DSDT") };
        for (int i = 0; i + 4 <= ids.Length; i += 4)
            tableIds.Add(BitConverter.ToUInt32(ids, i));

        foreach (uint id in tableIds.Distinct())
        {
            uint len = Kernel32.GetSystemFirmwareTable(Kernel32.ProviderAcpi, id, null, 0);
            if (len < 36)
                continue;
            var data = new byte[len];
            if (Kernel32.GetSystemFirmwareTable(Kernel32.ProviderAcpi, id, data, len) != len)
                continue;

            tables.Add(new AcpiTable
            {
                Signature = Encoding.ASCII.GetString(data, 0, 4),
                OemId = Encoding.ASCII.GetString(data, 10, 6).TrimEnd('\0', ' '),
                OemTableId = Encoding.ASCII.GetString(data, 16, 8).TrimEnd('\0', ' '),
                Length = data.Length,
                Data = data,
            });
        }
        return tables;
    }

    private static uint SignatureToId(string signature) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(signature));

    /// <summary>Tables with sensitive data (MSDM holds the Windows product key).</summary>
    private static readonly HashSet<string> Sensitive = ["MSDM", "SLIC"];

    /// <summary>Saves each table as &lt;signature&gt;.aml (ready for MaciASL / iasl).</summary>
    /// <returns>Number of tables written.</returns>
    public static int Export(IEnumerable<AcpiTable> tables, string directory)
    {
        Directory.CreateDirectory(directory);
        var exportable = tables.Where(t => !Sensitive.Contains(t.Signature)).ToList();
        foreach (var t in exportable)
            File.WriteAllBytes(Path.Combine(directory, $"{t.Signature}.aml"), t.Data);
        return exportable.Count;
    }
}
