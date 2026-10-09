# EasyOpenCore

A Windows assistant that scans your hardware, checks macOS compatibility and plans an OpenCore
EFI following the [Dortania OpenCore Install Guide](https://dortania.github.io/OpenCore-Install-Guide/).

Available in English (default) and Brazilian Portuguese — switch languages at any time from the sidebar.

## Requirements

- Windows 10 1809+ (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build

## Build and run

```powershell
dotnet build EasyOpenCore.sln

# GUI
dotnet run --project src/EasyOpenCore.App

# CLI: console summary, compatibility matrix and EFI plan
dotnet run --project src/EasyOpenCore.Cli -- --json report.json --acpi .\acpi --macos 15 --lang en

# CLI: build the EFI for the recommended version (or --macos N)
dotnet run --project src/EasyOpenCore.Cli -- --build .\out

# Tests
dotnet test
```

## Status

- [x] Full hardware dump (CPU via CPUID, GPU, audio, network, storage, USB, input, ACPI)
- [x] PCI (`PciRoot(...)`) and ACPI paths ready for `config.plist`
- [x] ACPI table export (`.aml`) and JSON report
- [x] Preflight checks (RAID/VMD, CSM, Secure Boot, MBR, AVX2...)
- [x] Compatibility matrix for every macOS version from High Sierra to Tahoe
- [x] EFI plan per macOS version: SMBIOS, kexts (in load order), SSDTs, boot-args, iGPU DeviceProperties
- [x] English / Português (Brasil)
- [x] EFI builder: downloads OpenCore and kexts from GitHub releases, generates SSDTs (AML) with the real ACPI paths, writes `config.plist` from the release's `Sample.plist`, validates with `ocvalidate`
- [x] AppleALC layout-id matched to the machine model, AMD Vanilla patches with the core count, macserial serials
- [ ] USB mapping (UTBMap.kext)
- [ ] Write the EFI to a USB drive and download the macOS recovery

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.

## Adding a language

Copy `src/EasyOpenCore.Core/Localization/en.json` to `<code>.json` (e.g. `es.json`), translate the
values and register the language in `Loc.Languages`. Missing keys fall back to English.
