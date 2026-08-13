# Pane

**A modern wallpaper manager built around multi-monitor Windows setups.**

Pane gives each display its own wallpaper or slideshow and lets you save complete desktop configurations as **Setups**. Switch between them without rebuilding everything monitor by monitor.

> **Pane is currently in preview.**
> Windows 10/11 · x64

<p align="center">
  <img src="docs/0.3.1 preview screenshots 1.png" alt="Pane main interface" width="90%">
  &nbsp;
</p>

## What Pane does

### Independent control for every monitor

Each connected display gets its own wallpaper configuration.

- Static wallpapers
- Independent per-monitor slideshows
- Fill, Fit, Stretch and Center
- Custom slideshow intervals
- Shuffle or sequential playback
- Loop control
- Soft-fade transitions
- Monitor-aware wallpaper previews

Pane uses Windows' native wallpaper APIs to apply wallpapers directly to individual displays.

### Adaptive Setups

<p>  <img src="docs/0.3.1 preview screenshots 2.png" alt="Pane Adaptive Setups" width="48%">
</p>

A **Setup** saves the wallpaper configuration for your desktop so you can switch between different groups of wallpapers and slideshows without reconfiguring every monitor.

For example:

- Daily
- Gaming
- Photography
- Night

Setups remember the wallpaper settings assigned to each display, along with the last-known monitor arrangement used to represent the Setup inside Pane.

Temporarily disconnected displays are retained instead of being thrown away, so their configuration is still there when they return.

If an old or no-longer-used display does need to be removed, Pane now includes **Manage displays** and safe cleanup for disconnected monitor entries.

Connected or ambiguously identified displays are protected from cleanup.

Switching Setups also includes a short **Undo** option so you can jump back to the previous Setup.

> Pane does not rearrange monitors in Windows. Display positioning shown inside Pane is a representation of the Windows desktop layout.

### Display-aware interface

Pane reads the displays currently connected to Windows and builds a visual representation of the desktop around them.

The interface accounts for:

- Monitor position
- Landscape and portrait orientation
- Resolution and aspect ratio
- Physical display size when reliable information is available
- Ultrawide displays
- Virtual displays
- Disconnected displays saved in a Setup

Wallpaper previews inside the editor use the selected monitor's actual aspect ratio, so a portrait, 16:9 or ultrawide display is previewed in the shape it will actually use.

Saved Setups also include a miniature view of their monitor arrangement and currently assigned wallpapers.

### Display styles

Pane can automatically choose a visual style for each detected display.

Current styles include:

- Standard Flat
- Ultrawide Flat
- Ultrawide Curved
- Large Display / TV
- Laptop / Built-in

The automatic result can be overridden per monitor when Windows cannot reliably identify the physical display type.

Display styles only affect how the monitor is represented inside Pane. They do not change Windows display settings.

### Monitor aliases

Windows monitor names are not always particularly useful.

Pane lets you rename displays to something that makes sense to you, such as:

- Main Monitor
- Backup
- Vertical
- TV
- Drawing Display

Aliases persist across Setups and remain separate from the monitor's underlying Windows identity.

### Disconnected display management

Pane intentionally remembers monitors that disappear from Windows instead of immediately deleting their saved settings.

This is useful for:

- Laptops moving between docks
- TVs that are not always connected
- Portable monitors
- Virtual displays
- Temporary multi-monitor arrangements

Saved displays are classified as **Connected**, **Disconnected**, or **Connection uncertain**.

Disconnected profiles can be removed individually or cleaned up in bulk. Pane re-checks connected displays before removal and will not remove a profile if it cannot safely determine that the display is disconnected.

Cleanup only removes Pane's saved configuration for that display.

It does **not**:

- Delete wallpaper files
- Delete slideshow folders
- Uninstall monitors
- Change Windows display configuration

### Runs in the notification area

Closing the Pane window does not stop active slideshows.

Pane can remain running from the Windows notification area and be reopened whenever you need to make a change.

---

## Download

The latest preview builds are available under **[Releases](https://github.com/yeawrongperson/Pane/releases)**.

Current preview:

```text
Pane-0.3.1-preview-win-x64.zip
