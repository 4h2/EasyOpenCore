using EasyOpenCore.Core;
using EasyOpenCore.Core.Checks;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Hardware;
using EasyOpenCore.Core.Localization;

// Usage:
//   eoc                       -> summary on the console
//   eoc --json report.json    -> save the full hardware report
//   eoc --acpi folder         -> export the ACPI tables (.aml)
//   eoc --macos 15            -> EFI plan for a specific macOS version (default: recommended)
//   eoc --lang pt-BR          -> output language (default: en)
//   eoc --build folder        -> download everything and build the EFI for --macos (or the recommended version)
Console.OutputEncoding = System.Text.Encoding.UTF8;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (Arg("--lang") is { } lang)
    Loc.Instance.SetLanguage(lang);

var report = HardwareScanner.Scan(new Progress<string>(s => Console.Error.WriteLine($"[scan] {s}")));

if (Arg("--json") is { } jsonPath)
{
    File.WriteAllText(jsonPath, ReportSerializer.ToJson(report));
    Console.WriteLine($"Report saved to {Path.GetFullPath(jsonPath)}");
}
if (Arg("--acpi") is { } acpiDir)
{
    int count = AcpiTableReader.Export(report.AcpiTables, acpiDir);
    Console.WriteLine($"{count} ACPI tables exported to {Path.GetFullPath(acpiDir)}");
}

var s = report.System;
Console.WriteLine($"""

== System ==
{s.Manufacturer} {s.Model} ({s.Chassis}) | Board: {s.BoardManufacturer} {s.BoardProduct}
BIOS {s.BiosVendor} {s.BiosVersion} ({s.BiosDate}) | Firmware: {s.Firmware} | Secure Boot: {s.SecureBootEnabled}
{s.OsName} build {s.OsBuild}

== CPU ==
{report.Cpu.Name}
{report.Cpu.Codename} | Family {report.Cpu.Family:X}h Model {report.Cpu.Model:X2}h Stepping {report.Cpu.Stepping} | {report.Cpu.Cores}C/{report.Cpu.Threads}T | Mobile: {report.Cpu.IsMobile} | Hybrid: {report.Cpu.IsHybrid}
Features: {string.Join(' ', report.Cpu.Features)}
""");

Console.WriteLine("== GPU ==");
foreach (var g in report.Gpus)
    Console.WriteLine($"{g.Name} [{g.VendorId}:{g.DeviceId}] {g.Kind} VRAM={g.VramBytes / 1048576} MB  {g.PciPath}  {g.AcpiPath}");

Console.WriteLine("\n== Memory ==");
foreach (var m in report.Memory)
    Console.WriteLine($"{m.Slot}: {m.CapacityBytes / 1073741824} GB {m.Type} {m.SpeedMts} MT/s {m.Manufacturer} {m.PartNumber}");

Console.WriteLine("\n== Storage ==");
foreach (var c in report.StorageControllers)
    Console.WriteLine($"Controller: {c.Name} [{c.VendorId}:{c.DeviceId}] CC={c.ClassCode} {c.PciPath}");
foreach (var d in report.Storage)
    Console.WriteLine($"Disk: {d.Model} {d.Bus} {d.Media} {d.SizeBytes / 1000000000} GB {d.PartitionStyle}{(d.IsSystemDisk ? " (system)" : "")}");

Console.WriteLine("\n== Audio ==");
foreach (var c in report.AudioControllers)
    Console.WriteLine($"Controller: {c.Name} [{c.VendorId}:{c.DeviceId}] {c.PciPath}");
foreach (var c in report.AudioCodecs)
    Console.WriteLine($"Codec: {c.Name} [{c.VendorId}:{c.DeviceId}] {c.CodecName}{(c.IsHdmi ? " (HDMI)" : "")}");

Console.WriteLine("\n== Network ==");
foreach (var n in report.Network)
    Console.WriteLine($"{n.Kind}: {n.Name} [{n.VendorId}:{n.DeviceId}] {n.Bus} {n.PciPath}");

Console.WriteLine("\n== Input ==");
foreach (var i in report.Input)
    Console.WriteLine($"{i.Type} ({i.Bus}): {i.Name} {i.HardwareId} {i.AcpiPath}");

Console.WriteLine("\n== Checks ==");
foreach (var f in PreflightChecks.Run(report))
    Console.WriteLine($"[{f.Severity}] {f.Title} — {f.Detail}");

// Compatibility matrix
var compat = CompatibilityEngine.Evaluate(report);
static string Mark(SupportLevel l) => l switch
{
    SupportLevel.Supported => "OK",
    SupportLevel.Patched => "P",
    SupportLevel.Limited => "L",
    SupportLevel.Unknown => "?",
    _ => "x",
};

Console.WriteLine("\n== Compatibility (OK supported · P patched · L limited · ? unverified · x unsupported) ==");
Console.WriteLine($"{"",-44}" + string.Concat(compat.Versions.Select(v => $"{v.Id,7}")));
Console.WriteLine($"{"Overall",-44}" + string.Concat(compat.Verdicts.Select(v => $"{Mark(v.Level),7}")));
foreach (var c in compat.Components)
{
    var label = $"{c.Component}: {c.Name}";
    if (label.Length > 43) label = label[..40] + "...";
    Console.WriteLine($"{label,-44}" + string.Concat(compat.Versions.Select(v => $"{Mark(c.Cells[v.Id].Level),7}")));
}

var target = Arg("--macos") ?? compat.RecommendedVersion;
Console.WriteLine($"\nRecommended: {compat.RecommendedVersion ?? "none"}");
if (target is not null && compat.Versions.Any(v => v.Id == target))
{
    var plan = EfiPlanner.Plan(report, compat, target);
    Console.WriteLine($"\n== EFI plan for {plan.Version.DisplayName} ==");
    Console.WriteLine($"SMBIOS: {plan.Smbios} — {plan.SmbiosReason}");
    Console.WriteLine("Kexts:");
    foreach (var k in plan.Kexts) Console.WriteLine($"  {k.Name,-28} {k.Reason}");
    Console.WriteLine("SSDTs:");
    foreach (var t in plan.Ssdts) Console.WriteLine($"  {t.Name,-28} {t.Reason}");
    Console.WriteLine("Boot-args:");
    foreach (var b in plan.BootArgs) Console.WriteLine($"  {b.Name,-28} {b.Reason}");
    foreach (var p in plan.DeviceProperties) Console.WriteLine($"DeviceProperties: {p.PciPath} {p.Key}={p.Value}");
    foreach (var w in plan.Warnings) Console.WriteLine($"Note: {w}");
}

Console.WriteLine($"\n{report.AllDevices.Count} devices enumerated. Errors: {report.ScanErrors.Count}");

if (Arg("--build") is { } buildDir && target is not null)
{
    using var github = new GitHubClient();
    var result = await new EfiBuilder(github).BuildAsync(report, compat, target, Path.GetFullPath(buildDir),
        new Progress<string>(s => Console.Error.WriteLine($"[build] {s}")));

    Console.WriteLine($"\n== EFI built in {result.OutputDirectory} (OpenCore {result.OpenCoreVersion}) ==");
    foreach (var k in result.Kexts) Console.WriteLine($"  {k.Name,-28} {k.Version,-10} {k.Source}");
    foreach (var k in result.MissingKexts) Console.WriteLine($"  MISSING {k.Name}: {k.Reason} {k.Url}");
    Console.WriteLine($"SSDTs: {string.Join(", ", result.Ssdts)}");
    foreach (var m in result.ManualSsdts) Console.WriteLine($"  Manual: {m.Name} — {Loc.T(m.ReasonKey)}");
    if (result.Audio is { } a) Console.WriteLine($"Audio: layout-id {a.LayoutId} at {a.ControllerPath}");
    Console.WriteLine($"ocvalidate: {(result.ValidationPassed ? "PASSED" : "FAILED")}\n{result.ValidationOutput}");
}
