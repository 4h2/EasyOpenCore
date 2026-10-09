# EasyOpenCore — Architecture

A Windows tool that scans the hardware, evaluates macOS compatibility and builds an OpenCore EFI
following the [Dortania OpenCore Install Guide](https://dortania.github.io/OpenCore-Install-Guide/).

## Stack

- **C# / .NET 8**, Windows 10 1809+ (`net8.0-windows10.0.17763.0`)
- **WPF** for the UI; **System.Management** (WMI) + P/Invoke (SetupAPI, kernel32) for hardware access
- No heavy dependencies: plist, downloads and ZIP handling use the .NET base library

## Projects

| Project | Role |
|---|---|
| `src/EasyOpenCore.Core` | All logic: scanner, checks, compatibility database and engine, EFI planner, localization. No UI. |
| `src/EasyOpenCore.App` | WPF UI (plain MVVM, no framework). |
| `src/EasyOpenCore.Cli` | `eoc.exe`: console dump, compatibility matrix and EFI plan; `--json`, `--acpi`, `--macos`, `--lang`. |
| `tests/EasyOpenCore.Core.Tests` | xUnit tests for the engine/planner (synthetic hardware profiles) and path conversions. |

## Pipeline

```
Scanner ─► HardwareReport ─► CompatibilityEngine ─► EfiPlanner (per macOS version) ─► EFI builder
 (done)        (done)              (done)                     (done)                    (next)
```

### 1. Scanner (`Core/Hardware`)

| Source | Data |
|---|---|
| CPUID (`X86Base.CpuId`) | Vendor, family/model/stepping → microarchitecture, SSE4.2/AVX2, hybrid CPU |
| SetupAPI (`PnpEnumerator`) | Every device: hardware IDs, PCI class, driver, **OpenCore `PciRoot(...)/Pci(...)` path** and **ACPI path** (`\_SB.PCI0.LPC0.EC0`) |
| WMI | System, board, BIOS, chassis, memory, disks (MSFT_PhysicalDisk/MSFT_Disk) |
| `GetSystemFirmwareTable` | DSDT and other ACPI tables (exportable as `.aml`; MSDM/SLIC are skipped because they hold the Windows key) |
| Registry | Secure Boot state, real GPU VRAM |

The scanner stores **language-neutral ids** (categories, ACPI roles, input types); the UI translates them.

### 2. Compatibility (`Core/Compatibility`)

The database is JSON embedded in the assembly (`Core/Data`):

| File | Content |
|---|---|
| `macos.json` | Supported versions (10.13 → 26 Tahoe, the last Intel release) |
| `smbios.json` | Mac models with the macOS range each one supports |
| `cpus.json` | Microarchitecture → version ranges, SMBIOS candidates (desktop / desktop+dGPU / laptop / laptop H-series), SSDTs, kexts |
| `gpus.json` | Intel iGPUs (with `AAPL,ig-platform-id` per variant), AMD families, NVIDIA by device-id ranges |
| `devices.json` | Audio codecs, Ethernet, Wi-Fi, Bluetooth, NVMe blacklist, input buses |
| `kexts.json` | Kext catalog: source repository and load order |

Rules are matched in file order (first match wins) by vendor, device-id list, device-id range,
name regex and GPU kind. Each rule has **version ranges** with a level:
`supported`, `patched`, `limited`, `unknown` (unverified) or `unsupported`, plus a note key.

`CompatibilityEngine` evaluates every component for every version. A version's overall level is
the minimum of CPU and the best usable GPU (on laptops, only the iGPU counts because it drives the
panel). The recommended version is the one with the best level, then fewest broken components,
then the newest.

`EfiPlanner` produces, for one target version:
- **SMBIOS**: first CPU candidate that supports the version, falling back to newer models
- **Kexts**: base set + CPU/laptop/device kexts whose ranges include the version, sorted by load order
- **SSDTs**: platform list + detected needs (AWAC/RTC, B550/A520 CPUR, I2C GPIO/XOSI, dGPU off, 300-series PMC)
- **Boot-args** and **DeviceProperties** for the iGPU (headless variant when a dGPU drives the displays)

### 3. Localization (`Core/Localization`)

`Loc` loads `en.json` (primary, fallback) and other languages from embedded JSON. The UI binds
through `{l:Tr key}` and refreshes live on language changes; data-file notes use `@key` references.
The chosen language is saved to `%AppData%\EasyOpenCore\settings.json`.

## Next steps

1. **EFI builder**: download OpenCorePkg and kexts from GitHub releases (cache + checksum), copy
   them into `EFI/OC`, generate `config.plist` from the release's `Sample.plist`, apply quirks per
   platform, compile/copy SSDTs, validate with `ocvalidate.exe`.
2. **Audio**: pick AppleALC layout-ids from the AppleALC repository data.
3. **USB mapping** on Windows (USBToolBox approach) generating `UTBMap.kext`.
4. Keep the database current: entries marked `unknown` need confirmation, especially for Tahoe.
