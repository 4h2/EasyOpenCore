using System.Runtime.Intrinsics.X86;
using System.Text;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Hardware;

public static class CpuDetector
{
    public static CpuInfo Detect()
    {
        var cpu = new CpuInfo();
        ReadCpuId(cpu);
        ReadWmi(cpu);
        cpu.Codename = ResolveCodename(cpu);
        cpu.IsMobile = IsMobile(cpu);
        return cpu;
    }

    private static void ReadCpuId(CpuInfo cpu)
    {
        if (!X86Base.IsSupported)
            return;

        var (maxLeaf, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        cpu.VendorString = Ascii(ebx, edx, ecx);
        cpu.Vendor = cpu.VendorString switch
        {
            "GenuineIntel" => CpuVendor.Intel,
            "AuthenticAMD" => CpuVendor.Amd,
            _ => CpuVendor.Unknown,
        };

        var (eax1, _, ecx1, edx1) = X86Base.CpuId(1, 0);
        int baseFamily = (eax1 >> 8) & 0xF;
        int baseModel = (eax1 >> 4) & 0xF;
        cpu.Stepping = eax1 & 0xF;
        cpu.Family = baseFamily == 0xF ? baseFamily + ((eax1 >> 20) & 0xFF) : baseFamily;
        cpu.Model = baseFamily is 0x6 or 0xF ? (((eax1 >> 16) & 0xF) << 4) | baseModel : baseModel;

        AddIf(cpu, (edx1 >> 26) & 1, "SSE2");
        AddIf(cpu, ecx1 & 1, "SSE3");
        AddIf(cpu, (ecx1 >> 9) & 1, "SSSE3");
        AddIf(cpu, (ecx1 >> 19) & 1, "SSE4.1");
        AddIf(cpu, (ecx1 >> 20) & 1, "SSE4.2");
        AddIf(cpu, (ecx1 >> 22) & 1, "MOVBE");
        AddIf(cpu, (ecx1 >> 23) & 1, "POPCNT");
        AddIf(cpu, (ecx1 >> 25) & 1, "AES");
        AddIf(cpu, (ecx1 >> 28) & 1, "AVX");
        AddIf(cpu, (ecx1 >> 5) & 1, "VMX");

        if (maxLeaf >= 7)
        {
            var (_, ebx7, _, edx7) = X86Base.CpuId(7, 0);
            AddIf(cpu, (ebx7 >> 3) & 1, "BMI1");
            AddIf(cpu, (ebx7 >> 5) & 1, "AVX2");
            AddIf(cpu, (ebx7 >> 8) & 1, "BMI2");
            AddIf(cpu, (ebx7 >> 16) & 1, "AVX512F");
            cpu.IsHybrid = ((edx7 >> 15) & 1) == 1;
        }

        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        if ((uint)maxExt >= 0x80000004)
        {
            var sb = new StringBuilder();
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), 0);
                sb.Append(Ascii(a, b, c, d));
            }
            cpu.Name = sb.ToString().Trim('\0', ' ');
        }
    }

    private static void ReadWmi(CpuInfo cpu)
    {
        foreach (var p in Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            cpu.Cores += Wmi.Int(p, "NumberOfCores");
            cpu.Threads += Wmi.Int(p, "NumberOfLogicalProcessors");
            if (cpu.Name.Length == 0)
                cpu.Name = Wmi.Str(p, "Name");
        }
    }

    private static void AddIf(CpuInfo cpu, int bit, string feature)
    {
        if (bit == 1)
            cpu.Features.Add(feature);
    }

    private static string Ascii(params int[] regs)
    {
        var bytes = regs.SelectMany(BitConverter.GetBytes).ToArray();
        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>
    /// Maps family/model/stepping to the microarchitecture names used by the Dortania guide.
    /// </summary>
    internal static string ResolveCodename(CpuInfo cpu)
    {
        if (cpu.Vendor == CpuVendor.Amd)
        {
            return cpu.Family switch
            {
                0x15 => "Bulldozer",
                0x16 => "Jaguar",
                0x17 when cpu.Model < 0x30 => "Zen / Zen+",
                0x17 => "Zen 2",
                0x19 when cpu.Model is (>= 0x10 and <= 0x1F) or (>= 0x60 and <= 0x7F) or >= 0xA0 => "Zen 4",
                0x19 => "Zen 3",
                0x1A => "Zen 5",
                _ => $"AMD Family {cpu.Family:X}h",
            };
        }

        if (cpu.Vendor != CpuVendor.Intel || cpu.Family != 6)
            return "";

        string name = cpu.Model switch
        {
            0x17 or 0x1D => "Penryn",
            0x1A or 0x1E or 0x1F or 0x2E => "Nehalem",
            0x25 or 0x2C or 0x2F => "Westmere",
            0x2A => "Sandy Bridge",
            0x2D => "Sandy Bridge-E",
            0x3A => "Ivy Bridge",
            0x3E => "Ivy Bridge-E",
            0x3C or 0x45 or 0x46 => "Haswell",
            0x3F => "Haswell-E",
            0x3D or 0x47 => "Broadwell",
            0x4F or 0x56 => "Broadwell-E",
            0x4E or 0x5E => "Skylake",
            0x55 => "Skylake-X / Cascade Lake",
            0x8E => cpu.Stepping switch
            {
                9 => "Kaby Lake",
                0xA => "Kaby Lake-R / Coffee Lake",
                0xB => "Whiskey Lake",
                _ => "Comet Lake",
            },
            0x9E => cpu.Stepping <= 9 ? "Kaby Lake" : "Coffee Lake",
            0x66 => "Cannon Lake",
            0x7D or 0x7E => "Ice Lake",
            0xA5 or 0xA6 => "Comet Lake",
            0x8C or 0x8D => "Tiger Lake",
            0xA7 => "Rocket Lake",
            0x97 or 0x9A or 0xBE => "Alder Lake",
            0xB7 or 0xBA or 0xBF => "Raptor Lake",
            0xAA or 0xAC => "Meteor Lake",
            0xBD => "Lunar Lake",
            0xC5 or 0xC6 => "Arrow Lake",
            0x37 or 0x4C => "Bay Trail / Braswell",
            0x5C => "Apollo Lake",
            0x7A => "Gemini Lake",
            0x96 or 0x9C => "Elkhart / Jasper Lake",
            _ => $"Intel Family 6 Model {cpu.Model:X2}h",
        };

        return name;
    }

    private static bool IsMobile(CpuInfo cpu)
    {
        if (cpu.Vendor == CpuVendor.Intel && cpu.Family == 6
            && cpu.Model is 0x45 or 0x3D or 0x4E or 0x8E or 0x7E or 0xA6 or 0x8C or 0x9A or 0xBA or 0xAA or 0xBD)
            return true;
        // Mobile model suffixes: i7-8550U, i5-1135G7, i7-12700H, Ryzen 7 5800HS...
        return System.Text.RegularExpressions.Regex.IsMatch(cpu.Name, @"\d{4,5}(U|H|HK|HX|HS|Y|G\d|P)\b");
    }
}
