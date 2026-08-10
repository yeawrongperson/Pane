# Pane

Pane is a focused Windows 10/11 wallpaper manager with independent static and slideshow profiles for every connected display.

## Current milestone

- Native WinUI 3 dark/glass dashboard and custom title bar
- Real monitor enumeration, topology, resolution, primary-display and refresh-rate detection
- Per-monitor profiles with JSON persistence in `%LOCALAPPDATA%\Pane`
- Native `IDesktopWallpaper` per-monitor static wallpaper application
- Non-recursive slideshow catalog, shuffle/sequential order, independent cancellable sessions
- Static/slideshow pickers, validation, preview, fit, interval, shuffle and loop controls

Soft Fade is represented in the profile and UI, but desktop overlay transitions and Identify overlays are intentionally not implemented yet. Wallpaper changes are immediate. Automatic hot-plug monitoring is future work; startup detection and Refresh are available. Closing the window keeps Pane running in the notification area so slideshows continue.

## Build

Requirements: Windows 10 19041+, x64, Visual Studio 2022 with Desktop development with C++ and Windows App SDK support, and the .NET 8 SDK.

```powershell
dotnet restore Wallflow.slnx
# Run from a Visual Studio Developer PowerShell:
msbuild Wallflow.slnx /p:Configuration=Debug /p:Platform=x64
dotnet test tests/Wallflow.Core.Tests/Wallflow.Core.Tests.csproj
# Launch src\Wallflow\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Pane.exe
```

Debug builds are unpackaged and framework-dependent for straightforward development launches.

## Building a Release

From the repository root:

```powershell
.\scripts\release.ps1
```

If Windows blocks local PowerShell scripts under its current execution policy, use the included launcher instead (it changes policy only for that process):

```powershell
.\scripts\release.cmd
```

To override the version for one release:

```powershell
.\scripts\release.ps1 -Version 0.1.1-beta
```

The default version is defined near the top of `Directory.Build.props`. Releases are always unpackaged, self-contained, x64, and built in Release configuration. Output is written to `artifacts\releases\`.

Distribute only the generated `Pane-<version>-win-x64.zip`. Do not distribute `bin`, `obj`, Debug output, test assemblies, or individual intermediate DLLs.

Pane beta builds are currently unsigned, so Windows SmartScreen may display an unknown-publisher warning. Clean-machine testing guidance is in `docs\RELEASE_TESTING.md`.
