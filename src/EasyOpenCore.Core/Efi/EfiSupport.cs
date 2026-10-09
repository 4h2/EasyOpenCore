using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Efi;

/// <summary>PlatformInfo identity: serial/MLB from OpenCore's macserial, a new UUID and the NIC MAC as ROM.</summary>
public static partial class SmbiosGenerator
{
    public static async Task<SmbiosIdentity> GenerateAsync(string macserialExe, string model, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(macserialExe, ["-m", model, "-g", "-n", "1"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start macserial");
        string output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var match = SerialLine().Match(output);
        if (!match.Success)
            throw new InvalidOperationException($"Unexpected macserial output: {output.Trim()}");

        return new SmbiosIdentity(model, match.Groups[1].Value, match.Groups[2].Value,
            Guid.NewGuid().ToString().ToUpperInvariant(), PrimaryMac());
    }

    /// <summary>MAC of the first physical Ethernet adapter (Dortania's recommendation for ROM), else a random local address.</summary>
    public static byte[] PrimaryMac()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .Select(n => n.GetPhysicalAddress().GetAddressBytes())
            .FirstOrDefault(b => b.Length == 6 && b.Any(x => x != 0));
        if (nic is not null)
            return nic;

        var random = new byte[6];
        Random.Shared.NextBytes(random);
        random[0] = (byte)((random[0] & 0xFE) | 0x02); // unicast, locally administered
        return random;
    }

    [GeneratedRegex(@"^\s*([A-Z0-9]{10,14})\s*\|\s*([A-Z0-9]{13,20})\s*$", RegexOptions.Multiline)]
    private static partial Regex SerialLine();
}

/// <summary>Picks an AppleALC layout-id from the codec's Info.plist in the AppleALC repository.</summary>
public static class AppleAlcLayouts
{
    public static async Task<AudioLayout?> PickAsync(GitHubClient github, HardwareReport hw, CancellationToken ct)
    {
        var codec = hw.AudioCodecs.FirstOrDefault(c => !c.IsHdmi && c.VendorId == "10EC" && c.CodecName.StartsWith("ALC"));
        if (codec is null)
            return null;

        // The analog codec hangs off the chipset HDA controller, not the GPU's HDMI audio function.
        var controller = hw.AudioControllers.FirstOrDefault(c => c.VendorId is not ("1002" or "10DE") && c.PciPath.Length > 0)
                         ?? hw.AudioControllers.FirstOrDefault(c => c.PciPath.Length > 0);
        if (controller is null)
            return null;

        PDict info;
        try
        {
            var xml = await github.GetStringAsync(
                $"https://raw.githubusercontent.com/acidanthera/AppleALC/master/Resources/{codec.CodecName}/Info.plist", ct);
            info = PlistSerializer.Parse(xml).AsDict;
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var layouts = ((info.TryGet("Files") as PDict)?.TryGet("Layouts") as PArray)?.Items
            .OfType<PDict>()
            .Select(l => ((int)((l.TryGet("Id") as PInteger)?.Value ?? 0), l.GetString("Comment") ?? ""))
            .Where(l => l.Item1 > 0)
            .ToList() ?? [];
        if (layouts.Count == 0)
            return null;

        // Prefer a layout whose comment names this machine (e.g. "Lenovo T480").
        var words = $"{hw.System.Manufacturer} {hw.System.Model} {hw.System.BoardProduct}"
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .ToList();
        var best = layouts
            .Select((l, index) => (l, index, score: words.Count(w => l.Item2.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.score).ThenBy(x => x.index)
            .First().l;

        return new AudioLayout(controller.PciPath, best.Item1, layouts);
    }
}

/// <summary>AMD Vanilla kernel patches with the physical core count filled in.</summary>
public static class AmdVanilla
{
    private const string PatchesUrl = "https://raw.githubusercontent.com/AMD-OSX/AMD_Vanilla/master/patches.plist";

    public static async Task<List<PNode>> LoadAsync(GitHubClient github, int coreCount, CancellationToken ct)
    {
        var root = PlistSerializer.Parse(await github.GetStringAsync(PatchesUrl, ct)).AsDict;
        var patches = (root.Dict("Kernel").TryGet("Patch") as PArray)?.Items.OfType<PDict>().ToList() ?? [];
        foreach (var p in patches)
        {
            // "Force cpuid_cores_per_package to constant (user-specified)": Replace is B8/BA 00 00 00 00,
            // the byte after the opcode holds the core count.
            if ((p.GetString("Comment") ?? "").Contains("cpuid_cores_per_package") && p.TryGet("Replace") is PData data && data.Value.Length > 1)
            {
                var bytes = (byte[])data.Value.Clone();
                bytes[1] = (byte)Math.Clamp(coreCount, 1, 255);
                p["Replace"] = P.Data(bytes);
            }
        }
        return [.. patches];
    }
}
