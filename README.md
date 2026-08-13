# Pane

**A modern wallpaper manager built around multi-monitor Windows setups.**

Pane gives each display its own wallpaper or slideshow and lets you save complete desktop configurations as **Setups**. Switch between them without rebuilding everything monitor by monitor.

> **Pane is currently in preview.**
> Windows 10/11 · x64



<p align="center">
  <img src="docs/0.3.1 preview screenshots_Main_1.png" alt="Pane main interface" width="48%">
  &nbsp;
  <img src="docs/0.3.1 preview screenshots_Main_2.png" alt="Pane Adaptive Setups" width="48%">
</p>

## What Pane does

### Independent control for every monitor

Each connected display gets its own configuration.

* Static wallpapers
* Independent slideshows
* Fill, Fit, Stretch and Center
* Custom slideshow intervals
* Shuffle or sequential playback
* Loop control
* Per-monitor previews

Pane uses Windows' native wallpaper APIs to apply wallpapers directly to individual displays.

### Adaptive Setups

A Setup saves the wallpaper configuration for your whole desktop.

Create different layouts for different uses and switch between them without having to reconfigure every screen.

For example:

* Daily
* Gaming PC
* Photography
* Night

Setups remember their own monitor wallpaper settings and display layout. Pane also keeps track of displays that are temporarily disconnected so their configuration is still there when they return.

Setup switching includes an **Undo** option in case you want to jump straight back to the previous desktop.

### Monitor aliases

Physical monitor names aren't always useful.

Pane lets you rename them to something that actually makes sense, such as:

* Main Monitor
* Vertical
* TV
* Drawing Display

Aliases persist across Setups and are kept separate from the monitor's underlying Windows identity.

### Display-aware interface

Pane reads the monitors connected to Windows and presents their actual desktop arrangement inside the app, including display position, orientation and resolution information.

Saved Setups also include a miniature view of their monitor arrangement and currently assigned wallpapers.

### Runs in the notification area

Closing the Pane window does not stop active slideshows.

Pane can remain running from the Windows notification area and be reopened when you need to make a change.

---

## Download

The latest preview builds are available under **[Releases](https://github.com/yeawrongperson/Pane/releases)**.

Download the Windows x64 ZIP:

```text
Pane-0.2.1-preview-win-x64.zip
```

Extract it somewhere you want to keep Pane, then run:

```text
Pane.exe
```

Pane is currently distributed as an unpackaged, self-contained Windows application. There is no installer in the preview release.

### Windows SmartScreen

Preview builds are currently **unsigned**.

Windows may show an **Unknown Publisher** or SmartScreen warning when opening Pane for the first time. Code signing is planned for a future release.

---

## Requirements

* Windows 10 version 2004 / build 19041 or newer
* Windows 11
* x64 PC

---

## Preview status

Pane is usable, but it is still early software.

The current preview focuses on getting the core multi-monitor experience right: wallpapers, independent slideshows, monitor detection, persistent configuration and Adaptive Setups.

There are still features I want to add, and parts of the interface may change as Pane develops.

Some areas planned for future versions include:

* Faster controls from the notification area
* More slideshow controls
* Automatic Setup switching
* Wallpaper transitions
* Additional drag-and-drop interactions
* Import/export and backup options
* General onboarding and settings improvements

No dates are attached to the roadmap.

---

## Data and configuration

Pane stores its configuration locally under:

```text
%LOCALAPPDATA%\Pane
```

This includes saved Setups, monitor configuration and wallpaper state.

Wallpaper files remain in their original locations. Pane references the files you select rather than maintaining a separate copy of your image library.

---

## Building from source

Pane is built with:

* C#
* .NET 8
* WinUI 3
* Windows App SDK

Development requirements:

* Windows 10 19041+ or Windows 11
* x64
* Visual Studio 2022
* .NET 8 SDK
* Windows App SDK development support

Restore the solution:

```powershell
dotnet restore Wallflow.slnx
```

From a Visual Studio Developer PowerShell, build x64:

```powershell
msbuild Wallflow.slnx /p:Configuration=Debug /p:Platform=x64
```

Run the core tests:

```powershell
dotnet test tests/Wallflow.Core.Tests/Wallflow.Core.Tests.csproj
```

Debug builds are unpackaged and framework-dependent.

---

## Building a release

Pane includes a release pipeline for producing the self-contained Windows build.

From the repository root:

```powershell
.\scripts\release.ps1
```

A specific version can also be supplied:

```powershell
.\scripts\release.ps1 -Version 0.2.1-preview
```

Release output is written to:

```text
artifacts\releases\
```

The distributable artifact is:

```text
Pane-<version>-win-x64.zip
```

Do not distribute files from `bin`, `obj`, Debug output or intermediate build directories.

The release pipeline builds Pane in Release/x64, runs the automated tests, publishes the self-contained application, performs an isolated smoke test and creates the final ZIP and SHA-256 checksum.

---

## Project status

Pane is currently developed and maintained as an independent project.

Bug reports and useful feedback are welcome while the preview develops.
