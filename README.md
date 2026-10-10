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
- [x] EFI builder: downloads OpenCore and kexts from GitHub releases, generates SSDTs (AML) with the real ACPI paths, writes `config.plist` from the release's `Sample.plist`, validates with `ocvalidate`; optional OpenCore DEBUG build that logs every boot and saves kernel panics to the USB drive
- [x] AppleALC layout-id matched to the machine model, AMD Vanilla patches with the core count, macserial serials
- [x] USB mapping: live port discovery through the Windows USB hub IOCTLs, USBToolBox-compatible UTBMap.kext
- [x] USB installer: copy EFI to a FAT32 drive (optional elevated format), download the macOS recovery from Apple with chunklist signature verification
- [x] Post-Install tab: 24 tweaks from the Dortania Post-Install guide applied to an existing EFI (verbose/debug clean-up, DEBUG log build swap, OpenCanopy GUI, boot chime, HiDPI, LauncherOption, ScanPolicy, Secure Boot, SIP, extra kexts), with a config backup and `ocvalidate` after every change
- [x] Troubleshooting tab: every section of the five Dortania troubleshooting pages (96 entries, in English like the guide), summarized from the guide and linked to the exact heading, flagged when specific to the scanned hardware, with one-click fixes that apply the matching tweak

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.

## Adding a language

Copy `src/EasyOpenCore.Core/Localization/en.json` to `<code>.json` (e.g. `es.json`) and
`help.en.json` to `help.<code>.json` (post-install texts; `troubleshooting.en.json` stays English, like the guide), translate the values
and register the language in `Loc.Languages`. Missing keys fall back to English.
