# Pane Release Size Audit

Audit date: 2026-08-10

This is an information-only audit of the known-good `0.1.0-beta` pipeline. No application code, dependency, project setting, publish profile, manifest, or release script was changed. A separate diagnostic publish was written under `artifacts/audit/` with single-file bundling disabled solely so that bundled files could be measured individually.

## Current baseline

| Item | Measured value |
| --- | ---: |
| Version | `0.1.0-beta` |
| Target framework | `net8.0-windows10.0.19041.0` |
| Runtime | .NET 8.0.18 plus WindowsDesktop 8.0.18 |
| Windows App SDK | 1.7.250606001 |
| Runtime identifier | `win-x64` |
| Architecture | x64 |
| Configuration | Release |
| Deployment model | Unpackaged, self-contained, single-file |
| `Pane.exe` | 263,660,120 bytes (251.45 MiB) |
| ZIP | 99,417,570 bytes (94.81 MiB) |
| Intermediate single-file publish | 263,674,352 bytes (251.46 MiB) |
| Intermediate publish files | 2: `Pane.exe` and a 14,232-byte `Wallflow.Core.pdb` |
| Final distribution files | 1: `Pane.exe` |
| SHA-256 | `388f127796ef6302f8d9e715b5a5ce07130eb0697bf9e9cc19d993fc6989832a` |

The unchanged `scripts/release.cmd`/`scripts/release.ps1` pipeline completed successfully: restore passed, Release build passed, all 3 tests passed, publish passed, smoke launch passed, and package validation passed. The release script correctly removes the project PDB before staging, so no PDB, test assembly, MSIX package, or source file is present in the final ZIP.

The diagnostic unbundled self-contained publish contained 813 files totaling 267,818,934 bytes (255.41 MiB). Its small size difference from the single executable is bundling/layout overhead and does not represent a different feature set.

Known-good release input hashes recorded during the audit:

| File | SHA-256 |
| --- | --- |
| `scripts/release.ps1` | `92740558305f007f784b4de825168a4c15e0bafbcf0af89d08ab0d9cf2f4581f` |
| `src/Wallflow/Properties/PublishProfiles/win-x64.pubxml` | `9850c5430a93eb3283a3d70043ad00e74dec7dca63ef60dc6fa6fb4704274911` |
| `src/Wallflow/Wallflow.csproj` | `d5a26a879d62e4a7d3cccb7fa996a6a260a805df72c867d448b0aab64f04d9b0` |
| `Directory.Build.props` | `8c8747d34766e72c4071044e0fddee7699226c02d6c61b93791f10514cc5214b` |

Git has no commits yet (`HEAD` is all zeros), the active branch is `main`, and the repository contents are untracked. These hashes are therefore the only recorded baseline; Git cannot currently restore an individual known-good file.

## Major size contributors

The following attribution is measured from the 255.41 MiB diagnostic unbundled publish. Runtime-pack files were matched by name and length against the restored .NET runtime packs. Windows App SDK self-contained files are partly copied/generated through MSBuild staging, so that platform layer is grouped by origin and file family rather than claimed as exact package-level accounting.

| Rank | Contributor | Measured size | Share | Notes |
| ---: | --- | ---: | ---: | --- |
| 1 | .NET WindowsDesktop runtime pack | 89.55 MiB | 35.1% | Pulled by `Microsoft.WindowsDesktop.App.WindowsForms`; includes WinForms, WPF, drawing, desktop resources, and native presentation binaries. |
| 2 | .NET core runtime pack | 70.33 MiB | 27.5% | CoreCLR, JIT, base class libraries, networking, JSON, diagnostics, and native runtime support required by self-contained deployment. |
| 3 | WinUI / Windows App SDK self-contained payload, including WebView2 projections | approximately 70.20 MiB | 27.5% | WinUI XAML, Windows App Runtime native components, controls, projections, resources, and WebView2 loader/projection files. |
| 4 | Windows SDK .NET/WinRT projection | 23.73 MiB | 9.3% | `Microsoft.Windows.SDK.NET.dll`, required for projected Windows APIs used by WinUI and Pane. |
| 5 | Pane application, generated metadata, and assets | 1.60 MiB | 0.6% | Pane code, Wallflow.Core, apphost, dependency metadata, icon, and PNG assets. |

The largest individual files include:

| File | Size | Origin |
| --- | ---: | --- |
| `Microsoft.Windows.SDK.NET.dll` | 23.73 MiB | Windows SDK .NET projection |
| `PresentationFramework.dll` | 15.38 MiB | WindowsDesktop/WPF |
| `Microsoft.ui.xaml.dll` | 14.37 MiB | WinUI runtime |
| `System.Windows.Forms.dll` | 12.94 MiB | WindowsDesktop/WinForms |
| `System.Private.CoreLib.dll` | 12.56 MiB | .NET runtime |
| `PresentationCore.dll` | 8.15 MiB | WindowsDesktop/WPF |
| `System.Private.Xml.dll` | 7.63 MiB | .NET runtime |
| `Microsoft.WinUI.dll` | 6.97 MiB | Windows App SDK |
| `Microsoft.UI.Xaml.Controls.dll` | 6.31 MiB | WinUI controls |
| `System.Windows.Forms.Design.dll` | 5.31 MiB | WindowsDesktop runtime pack |

Within the 89.55 MiB WindowsDesktop payload, filename grouping measured approximately 50.50 MiB of WPF/desktop presentation files, 27.81 MiB of Windows Forms files, 1.59 MiB of drawing/SystemEvents files, and 9.65 MiB of other WindowsDesktop content. Pane has no WPF source usage; the WPF files arrive because the untrimmed self-contained WindowsDesktop runtime pack is deployed as a whole.

Culture/resource subdirectories total 19.01 MiB across the platform payloads. They are real localized framework/WinUI resources rather than Pane debug output. Restricting them could affect non-English Windows installations and should be tested, not assumed safe.

## Direct dependencies

| Dependency | Purpose | Used by | Required? | Approximate size impact | Removal risk | Recommendation |
| --- | --- | --- | --- | ---: | --- | --- |
| `Microsoft.WindowsAppSDK` 1.7.250606001 | WinUI 3, XAML, AppWindow, Windows pickers/projections, and unpackaged Windows App Runtime support | `App.xaml`, `MainWindow.xaml`, `MainWindow.xaml.cs`, generated XAML | Yes | About 70.20 MiB for App SDK/WinUI/WebView payload, plus the related 23.73 MiB Windows SDK projection | HIGH | Keep. It is Pane's UI platform. |
| `Microsoft.WindowsDesktop.App.WindowsForms` | Supplies the Windows Forms framework used for the notification-area icon and context menu | `TrayIconService.cs`: `NotifyIcon`, `ContextMenuStrip`, `ToolStripMenuItem`, `ToolStripSeparator`, `MouseButtons` | Yes, for the current tray implementation | 89.55 MiB WindowsDesktop pack; not all of that is actively used | HIGH | Keep now. A direct Win32 tray rewrite could remove this framework reference, but no such replacement exists in the project and tray/background behavior is release-critical. |
| `System.Drawing.Common` 8.0.0 | Image decode, resize/crop, high-quality drawing, PNG output, and tray icon extraction | `NativeServices.cs`: `Image`, `Bitmap`, `Graphics`, drawing modes, `ImageFormat`; `TrayIconService.cs`: `Icon`, `SystemIcons` | Yes, for current wallpaper rendering and tray icon code | Drawing/SystemEvents files are about 1.59 MiB. The explicit package currently adds approximately zero incremental runtime bytes because WindowsDesktop 8.0.18 supplies the deployed assemblies. | HIGH for removing the API; LOW/MEDIUM for merely testing removal of the redundant explicit package reference | Keep. Removing the package reference alone would not materially shrink the current output; replacing the API risks wallpaper fit/rendering and tray icon regressions. |
| `Wallflow.Core` project | Models, profile persistence, image catalog, slideshow session, and interfaces | Main Pane project throughout | Yes | `Wallflow.Core.dll` is 19,968 bytes (19.5 KiB) | HIGH functional risk, negligible size benefit | Keep. It is not a size problem. |
| `Microsoft.NETCore.App` runtime | Managed runtime and base class libraries | Entire application | Yes for self-contained distribution | 70.33 MiB | HIGH | Keep while “no separate .NET install” remains a requirement. |
| Windows SDK .NET projection | Managed access to Windows Runtime and Windows APIs | WinUI and Pane Windows API usage | Yes | 23.73 MiB | HIGH | Keep. Do not manually exclude it. |

### Windows Forms conclusion

Windows Forms is directly required by the current implementation, but only for `TrayIconService.cs`. Pane does not use Windows Forms controls for its main UI. There is no existing `Shell_NotifyIcon`/pure-Win32 tray implementation in the project. Pane already contains direct P/Invoke and COM code for monitor and wallpaper work, so a native replacement is architecturally possible, but it would be new behavior-sensitive code rather than dependency cleanup. Removing Windows Forms today would break notification-area operation, its menu, balloon notification, and background reopening/exiting.

The unusually large impact is not just `System.Windows.Forms.dll`: the untrimmed self-contained publish brings the complete WindowsDesktop runtime pack, including roughly 50.50 MiB of WPF/presentation content that Pane does not directly use. A successful future tray rewrite followed by removing the framework reference could plausibly save about 80–90 MiB uncompressed. ZIP savings would likely be materially smaller, roughly 20–35 MiB, and must be measured in an isolated experiment.

### System.Drawing.Common conclusion

System.Drawing is directly required twice:

1. `WallpaperImageRenderer` decodes user images, produces monitor-sized bitmaps for independent Fill/Fit/Center/Stretch behavior, performs high-quality interpolation, and writes cached PNGs.
2. `TrayIconService` extracts the executable icon and supplies a fallback icon.

The WinUI `BitmapImage` usage in `MainWindow.xaml.cs` is preview-only; it does not render and save the monitor-specific wallpaper files. No Windows Imaging Component, Win2D, or WinRT `BitmapEncoder` replacement exists in the project. Therefore System.Drawing cannot be removed without replacing tested wallpaper behavior. Its explicit NuGet package entry appears redundant while WindowsForms brings WindowsDesktop, but removing that line alone offers no meaningful release-size reduction and is not recommended as the first experiment.

## Transitive dependencies

| Dependency | Source | Runtime effect | Assessment |
| --- | --- | ---: | --- |
| `Microsoft.Web.WebView2` 1.0.2903.40 | Transitive from Windows App SDK | 1.53 MiB (`Microsoft.Web.WebView2.Core.dll`, projection DLL, and `WebView2Loader.dll`) | Pane has no direct WebView2 usage. It is suspicious from a feature perspective but package-managed by Windows App SDK; do not manually delete it without a supported exclusion and full WinUI regression test. |
| `Microsoft.Win32.SystemEvents` 8.0.0 | Transitive from System.Drawing.Common | 94.3 KiB deployed from the WindowsDesktop runtime pack | Small and part of the current drawing/desktop dependency set. Not worth targeting alone. |
| `Microsoft.Windows.SDK.BuildTools` 10.0.22621.756 | Transitive from Windows App SDK | Build-time only in this audit | Not present as a distinct runtime payload; not a distribution concern. |
| `Microsoft.NET.ILLink.Tasks` 8.0.18 | SDK auto-reference | Build-time only; trimming is disabled | Expected and not causing a separate shipped library. |
| MSTest/Test SDK graph | Tests project only | None in final ZIP | Correctly excluded by the release pipeline. |

No duplicate framework trees or duplicate assembly names were found in the unbundled root. Exact-content duplicate analysis found one 1.28 MiB pair (`mscordaccore.dll` and its architecture/version-named copy), both supplied by the standard .NET runtime pack, plus about 65 KiB of duplicate icon/splash PNG content. These should not be manually deleted from a self-contained runtime.

The self-contained Windows App SDK payload includes components whose names do not correspond to Pane features, such as widgets, AI workloads, deployment extensions, and broad localized resources. They are delivered by the supported Windows App SDK self-contained targets. Selectively deleting native binaries from that set is high risk because package initialization and XAML can load components dynamically.

## Assets and release hygiene

- The diagnostic output contains 51 files under `Assets`, totaling 725,207 bytes (708.2 KiB): `Pane.ico` plus 50 PNGs.
- `Pane.ico` is required by `MainWindow.xaml.cs` and release icon handling.
- `Square44x44Logo.altform-unplated_targetsize-32.png` is directly used in the title bar.
- The remaining PNG scale/target-size variants are principally future MSIX/Start/tile/splash assets referenced by `Package.appxmanifest`. They are copied into the portable build by the broad `Assets\*.png` content rule even though the portable runtime does not directly reference them.
- Excluding only unused MSIX PNG variants from the portable publish could save at most about 0.49 MiB before ZIP compression. The source assets and manifest should remain for future MSIX support.
- `Pane.Shortcut.ico` is not copied into the current publish.
- No project PDB, source, test assembly, `obj`, Git data, or MSIX package is in the final ZIP.
- The single-file intermediate does contain `Wallflow.Core.pdb` (14,232 bytes), but `release.ps1` intentionally excludes it from staging and the final distribution.
- Runtime diagnostic binaries such as `mscordaccore` are standard .NET runtime-pack content, not evidence that Pane was built in Debug configuration.

## Runtime overhead

Measured application payload is tiny relative to the platform:

- Pane/Wallflow code: `Pane.dll` is 390.0 KiB and `Wallflow.Core.dll` is 19.5 KiB.
- Pane apphost, dependency metadata, runtime configuration, and assets bring the application-specific group to about 1.60 MiB.
- .NET core runtime: 70.33 MiB measured.
- WindowsDesktop: 89.55 MiB measured.
- WinUI/Windows App SDK/WebView and Windows SDK projections: approximately 93.93 MiB combined.

Therefore roughly 99.4% of the unbundled publish is framework/runtime/platform content and roughly 0.6% is Pane-specific. Pane is large primarily because it is simultaneously self-contained for .NET, self-contained for Windows App SDK, untrimmed, and dependent on the WindowsDesktop runtime for a Windows Forms tray implementation. It is not large because of Wallflow.Core, user images, test binaries, or Pane's own code.

## Optimization opportunities

No optimization below was implemented.

### SAFE

1. **Whitelist portable assets while retaining all source/MSIX assets.** Copy only `Pane.ico` and the title-bar PNG into the portable publish. Expected saving: at most about 0.49 MiB from `Pane.exe`, likely roughly 0.1–0.3 MiB from the ZIP. Very small, but highly reversible and easy to verify.
2. **Evaluate single-file assembly compression as an isolated benchmark.** This may reduce the standalone EXE, but the ZIP already compresses it, so final download savings may be small and startup CPU/time can increase. Do not enable without timing startup and comparing the ZIP.

### MODERATE

1. **Experiment with limiting satellite languages.** Culture/resource directories total 19.01 MiB. Potential savings are meaningful, but Windows App SDK and WindowsDesktop resources may be needed on non-English systems. Test English and non-English Windows before adopting.
2. **Investigate a supported way to omit WebView2 when no WebView is used.** Maximum measured payload is only 1.53 MiB and unsupported deletion is not acceptable.
3. **Test whether the explicit `System.Drawing.Common` package reference is redundant.** Expected release-size reduction is approximately zero while WindowsDesktop remains, so this is dependency hygiene rather than a size optimization.

### RISKY

1. **Replace the Windows Forms tray implementation with direct Win32 `Shell_NotifyIcon` code, then remove `Microsoft.WindowsDesktop.App.WindowsForms`.** Potential saving is approximately 80–90 MiB uncompressed and perhaps 20–35 MiB in the ZIP. Risk is HIGH because tray lifetime, menus, notifications, hidden-window behavior, DPI, Explorer restarts, and clean shutdown must all be reimplemented and tested.
2. **Replace System.Drawing wallpaper rendering.** Potential additional saving after WindowsDesktop removal is only around 1.6 MiB, while regression risk to Fill/Fit/Center/Stretch, image decoding, and slideshow output is HIGH.
3. **Enable trimming.** This could remove substantial unused framework code, but WinUI/XAML, COM, reflection, and dynamically loaded Windows App SDK components are trimming-sensitive. Do not enable without a dedicated test matrix and fallback artifact.
4. **Switch to framework-dependent deployment.** This would remove most bundled runtime content but violates the current requirement that recipients install neither .NET nor Windows App Runtime.
5. **Manually delete Windows App SDK native files.** Do not do this. File names are not a reliable indicator of dynamic runtime requirements.

## Recommended next experiment

Test a **portable-publish asset whitelist** first, leaving the manifest and every source asset intact. It has the best combination of low regression risk, low difficulty, reversibility, and obvious before/after verification. The expected reduction is small—about 0.49 MiB in the executable and roughly 0.1–0.3 MiB in the ZIP—but it establishes a safe experimental method without touching Pane behavior or dependency resolution.

After that low-risk experiment, the only clearly large opportunity is the Windows Forms/WindowsDesktop dependency. It should be treated as a separate feature-preservation project, not a package-removal task.

## Audit conclusion

- **What makes Pane large:** approximately 89.55 MiB WindowsDesktop, 70.33 MiB .NET core, and approximately 93.93 MiB WinUI/Windows App SDK/Windows SDK projection content. Pane itself is about 1.60 MiB.
- **Is Windows Forms required?** Yes, by the current notification-area implementation in `TrayIconService.cs`.
- **Is System.Drawing.Common required?** Yes, by wallpaper rendering and tray icon handling. Its explicit package declaration has approximately zero incremental size while WindowsDesktop remains.
- **Safest first optimization:** portable-publish asset whitelisting.
- **Estimated first saving:** about 0.49 MiB uncompressed and roughly 0.1–0.3 MiB in the ZIP.
- **Large but risky opportunity:** replace the tray implementation and remove WindowsDesktop, potentially saving roughly 80–90 MiB uncompressed.

