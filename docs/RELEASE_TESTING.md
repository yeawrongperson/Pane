# Pane Release Testing

The portable release is self-contained, but a successful launch on the development PC does not prove that it works on a clean computer. Test the ZIP on hardware that does not have Visual Studio, the .NET SDK, or a separately installed Windows App Runtime.

## Clean-machine matrix

- Windows 10 x64, current supported update
- Windows 11 x64, current supported update
- No Visual Studio or Build Tools
- No .NET SDK
- No separately installed Windows App Runtime
- Standard user account; no administrator elevation

## Functional checklist

- Extract the entire ZIP before launching.
- Confirm `Pane.exe` displays the correct Pane icon.
- Launch Pane and confirm no console window appears.
- Confirm connected monitors are detected at startup.
- Apply a static wallpaper independently to each monitor.
- Configure and run independent slideshows on multiple monitors.
- Verify slideshow interval, shuffle, loop, and fit behavior.
- Verify available transition behavior; desktop transitions are currently immediate.
- Close with X and confirm Pane remains in the notification area.
- Reopen from the notification icon and confirm settings remain.
- Exit from the notification icon, reopen Pane, and confirm configured slideshows resume.
- Reboot Windows and manually launch Pane; startup launch is not currently implemented.
- Test a missing static wallpaper file.
- Test a missing or empty slideshow folder.
- Disconnect/reconnect a monitor and use Refresh Displays.
- Confirm settings are stored under `%LocalAppData%\Pane`, not beside `Pane.exe`.

## Security and signing

Pane beta builds are currently unsigned. Windows SmartScreen or antivirus software may show an unknown-publisher or unrecognized-app warning. Do not disable security controls. Production distribution should add a trusted code-signing certificate later.
