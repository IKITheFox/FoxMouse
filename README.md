<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/branding/source/foxmouse-mark-dark.png">
    <img src="assets/branding/source/foxmouse-mark-color.png" width="180" alt="FoxMouse logo">
  </picture>
</p>

# FoxMouse

English | [简体中文](README.zh-CN.md)

**v1.0.0 Beta** · [Download](https://github.com/IKITheFox/FoxMouse/releases/latest) · [Issues](https://github.com/IKITheFox/FoxMouse/issues) · [Changelog](CHANGELOG.md)

A Windows tray utility that helps locate the pointer by enlarging it during rapid back-and-forth movement, then smoothly restoring its size.

## Features

- Native cursor enlargement and an optional locator ring.
- Adjustable sensitivity and scale, application exclusions, and pause options.
- English and Simplified Chinese settings with system-aware themes.
- Online and self-contained offline installers with directory selection, repair, and uninstall.

## Installation and usage

Use the online installer when Microsoft runtime downloads are available; use the larger offline installer when bundled runtimes are needed. Source archives are not executable installation packages. Current candidate binaries are unsigned.

Select a language and installation folder, complete installation, and click OK. Shake the pointer back and forth; a single fast straight movement is not the trigger. Open settings from the tray. Run Setup again for maintenance, or uninstall through Windows Installed Apps or the root uninstaller. Completion remains visible until acknowledged.

Windows 11 x64 is the primary target. Beta does not certify every Windows 10, remote desktop, display configuration, or custom cursor. See [limitations](docs/known-limitations.md) and [support matrix](docs/support-matrix.md). Secure desktops and exclusive full-screen applications can restrict effects.

## Development

Use Windows x64, the .NET 10 SDK, and the Windows build tools required by the WinUI projects. From PowerShell:

```powershell
dotnet restore FoxMouse.slnx
dotnet test FoxMouse.slnx -c Release
./scripts/New-Release.ps1 -ExpectedVersion 1.0.0 -CandidateOnly
./scripts/New-OnlinePackage.ps1
```

Run packaging commands sequentially because they share build outputs. Desktop tests require an interactive Windows session; automated tests do not replace clean-machine or real-hardware acceptance.

## Privacy, contributing, and license

Read [privacy](docs/privacy.md), [contributing](CONTRIBUTING.md), and [security](SECURITY.md). Remove private paths and identifiers before sharing diagnostics. The repository retains its [GPL v3 license](LICENSE); third-party terms remain applicable as described in [license notices](LICENSE-NOTICE.md). FoxMouse is not affiliated with Apple.
