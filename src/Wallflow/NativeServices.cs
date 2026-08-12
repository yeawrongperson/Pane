using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Management;
using Wallflow.Core;

namespace Wallflow;

internal sealed class WindowsMonitorService : IMonitorService
{
    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken token = default)
    {
        var displays = new List<MonitorInfo>(); var index = 0;
        var wallpaperTargets = GetWallpaperTargets();
        var displayMetadata = GetDisplayConfigMetadata();
        var edidPhysicalSizes = GetEdidPhysicalSizes(displayMetadata.Values);
        Native.EnumDisplayMonitors(nint.Zero, nint.Zero, (handle, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            var mode = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>(), dmDeviceName = string.Empty, dmFormName = string.Empty };
            var hasCurrentMode = Native.EnumDisplaySettings(info.szDevice, Native.ENUM_CURRENT_SETTINGS, ref mode);
            var number = ++index; var width = info.rcMonitor.Right - info.rcMonitor.Left; var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var wallpaperId = wallpaperTargets.FirstOrDefault(target => target.Rect.Left == info.rcMonitor.Left && target.Rect.Top == info.rcMonitor.Top && target.Rect.Right == info.rcMonitor.Right && target.Rect.Bottom == info.rcMonitor.Bottom).Id;
            displayMetadata.TryGetValue(info.szDevice, out var metadata);
            var gdiPhysicalSize = GetGdiEstimatedPhysicalSize(info.szDevice);
            edidPhysicalSizes.TryGetValue(metadata?.MonitorDevicePath ?? string.Empty, out var edidPhysicalSize);
            var hasEdidPhysicalSize = !string.IsNullOrWhiteSpace(metadata?.MonitorDevicePath) &&
                edidPhysicalSize is not null;
            var physicalWidth = edidPhysicalSize?.WidthMillimeters ?? gdiPhysicalSize.WidthMillimeters;
            var physicalHeight = edidPhysicalSize?.HeightMillimeters ?? gdiPhysicalSize.HeightMillimeters;
            var physicalSizeSource = hasEdidPhysicalSize
                ? PhysicalSizeSource.EdidReported
                : physicalWidth is not null && physicalHeight is not null
                    ? PhysicalSizeSource.GdiEstimated
                    : PhysicalSizeSource.None;
            var friendlyName = string.IsNullOrWhiteSpace(metadata?.FriendlyName) ? $"Display {number}" : metadata.FriendlyName;
            displays.Add(new(
                wallpaperId ?? info.szDevice,
                info.szDevice,
                friendlyName,
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                width,
                height,
                (info.dwFlags & 1) != 0,
                hasCurrentMode && mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0,
                GetDisplayConfigOrientation(metadata?.Rotation, width, height) ??
                    GetDevModeOrientation(hasCurrentMode ? mode.dmDisplayOrientation : -1, width, height),
                metadata?.FriendlyName,
                metadata?.ManufacturerId,
                metadata?.ProductCode,
                metadata?.MonitorDevicePath,
                physicalWidth,
                physicalHeight,
                physicalSizeSource,
                metadata?.IsInternal));
            return true;
        }, nint.Zero);
        return Task.FromResult<IReadOnlyList<MonitorInfo>>(displays);
    }

#if DEBUG
    internal async Task<string> GetDiagnosticSnapshotAsync(
        Func<MonitorInfo, MonitorVisualPreference?>? preferenceResolver = null,
        CancellationToken token = default)
    {
        var monitors = await GetMonitorsAsync(token);
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            monitors.Select(monitor => MonitorDiagnosticFormatter.Format(
                monitor,
                MonitorVisualResolver.Resolve(monitor, preferenceResolver?.Invoke(monitor)))));
    }
#endif

    private static Dictionary<string, DisplayConfigMetadata> GetDisplayConfigMetadata()
    {
        var result = new Dictionary<string, DisplayConfigMetadata>(StringComparer.OrdinalIgnoreCase);
        var ambiguousSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != Native.ERROR_SUCCESS)
                    return result;
                var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
                var queryResult = Native.QueryDisplayConfig(
                    Native.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    nint.Zero);
                if (queryResult == Native.ERROR_INSUFFICIENT_BUFFER) continue;
                if (queryResult != Native.ERROR_SUCCESS) return result;

                for (var index = 0; index < pathCount; index++)
                {
                    var path = paths[index];
                    var sourceName = Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME.Create(path.sourceInfo.adapterId, path.sourceInfo.id);
                    if (Native.DisplayConfigGetDeviceInfo(ref sourceName) != Native.ERROR_SUCCESS ||
                        string.IsNullOrWhiteSpace(sourceName.viewGdiDeviceName)) continue;

                    var targetName = Native.DISPLAYCONFIG_TARGET_DEVICE_NAME.Create(path.targetInfo.adapterId, path.targetInfo.id);
                    if (Native.DisplayConfigGetDeviceInfo(ref targetName) != Native.ERROR_SUCCESS) continue;
                    if (ambiguousSources.Contains(sourceName.viewGdiDeviceName)) continue;
                    if (result.ContainsKey(sourceName.viewGdiDeviceName))
                    {
                        result.Remove(sourceName.viewGdiDeviceName);
                        ambiguousSources.Add(sourceName.viewGdiDeviceName);
                        continue;
                    }
                    var hasEdidIds = (targetName.flags & Native.DISPLAYCONFIG_TARGET_NAME_EDID_IDS_VALID) != 0;
                    result[sourceName.viewGdiDeviceName] = new(
                        NullIfWhiteSpace(targetName.monitorFriendlyDeviceName),
                        NullIfWhiteSpace(targetName.monitorDevicePath),
                        hasEdidIds ? targetName.edidManufactureId.ToString("X4") : null,
                        hasEdidIds ? targetName.edidProductCodeId.ToString("X4") : null,
                        path.targetInfo.rotation,
                        GetInternalState(targetName.outputTechnology));
                }
                return result;
            }
        }
        catch
        {
            return result;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, MatchedMonitorPhysicalSize> GetEdidPhysicalSizes(
        IEnumerable<DisplayConfigMetadata> displayMetadata)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT Active, InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams");
            using var collection = searcher.Get();
            var wmiMonitors = new List<WmiMonitorPhysicalSize>();
            foreach (ManagementObject item in collection)
            {
                using (item)
                {
                    var instanceName = item["InstanceName"] as string;
                    if (string.IsNullOrWhiteSpace(instanceName)) continue;
                    wmiMonitors.Add(new(
                        instanceName,
                        item["Active"] is true,
                        Convert.ToInt32(item["MaxHorizontalImageSize"] ?? 0),
                        Convert.ToInt32(item["MaxVerticalImageSize"] ?? 0)));
                }
            }
            return MonitorPhysicalSizeIdentityMatcher.Match(
                displayMetadata.Select(metadata => metadata.MonitorDevicePath).OfType<string>(),
                wmiMonitors);
        }
        catch { return new Dictionary<string, MatchedMonitorPhysicalSize>(StringComparer.OrdinalIgnoreCase); }
    }

    private static (int? WidthMillimeters, int? HeightMillimeters) GetGdiEstimatedPhysicalSize(string deviceName)
    {
        try
        {
            var deviceContext = Native.CreateDC("DISPLAY", deviceName, null, nint.Zero);
            if (deviceContext == nint.Zero) return (null, null);
            try
            {
                var width = Native.GetDeviceCaps(deviceContext, Native.HORZSIZE);
                var height = Native.GetDeviceCaps(deviceContext, Native.VERTSIZE);
                return (width > 0 ? width : null, height > 0 ? height : null);
            }
            finally { Native.DeleteDC(deviceContext); }
        }
        catch { return (null, null); }
    }

    private static DisplayOrientation GetDevModeOrientation(int rotation, int width, int height)
        => DisplayOrientationResolver.Resolve(rotation is >= 0 and <= 3 ? rotation * 90 : null, width, height);

    private static DisplayOrientation? GetDisplayConfigOrientation(uint? rotation, int width, int height)
        => rotation switch
        {
            Native.DISPLAYCONFIG_ROTATION_IDENTITY => DisplayOrientationResolver.Resolve(0, width, height),
            Native.DISPLAYCONFIG_ROTATION_ROTATE90 => DisplayOrientationResolver.Resolve(90, width, height),
            Native.DISPLAYCONFIG_ROTATION_ROTATE180 => DisplayOrientationResolver.Resolve(180, width, height),
            Native.DISPLAYCONFIG_ROTATION_ROTATE270 => DisplayOrientationResolver.Resolve(270, width, height),
            _ => null
        };

    private static bool? GetInternalState(int outputTechnology)
        => outputTechnology switch
        {
            Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS or
            Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED or
            Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED or
            Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => true,
            Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER or
            Native.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UNINITIALIZED => null,
            _ => false
        };

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record DisplayConfigMetadata(
        string? FriendlyName,
        string? MonitorDevicePath,
        string? ManufacturerId,
        string? ProductCode,
        uint? Rotation,
        bool? IsInternal);

    private static List<(string Id, Native.RECT Rect)> GetWallpaperTargets()
    {
        var result = new List<(string, Native.RECT)>();
        IDesktopWallpaper? desktop = null;
        try
        {
            desktop = (IDesktopWallpaper)new DesktopWallpaper(); desktop.GetMonitorDevicePathCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                desktop.GetMonitorDevicePathAt(i, out var pointer);
                try
                {
                    var id = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrWhiteSpace(id)) { desktop.GetMonitorRECT(id, out var rect); result.Add((id, rect)); }
                }
                finally { if (pointer != nint.Zero) Marshal.FreeCoTaskMem(pointer); }
            }
        }
        catch (COMException) { }
        finally { if (desktop is not null) Marshal.FinalReleaseComObject(desktop); }
        return result;
    }
}

internal sealed class DesktopWallpaperService : IWallpaperService
{
    public async Task SetWallpaperAsync(string monitorId, string imagePath, WallpaperFit fit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (!File.Exists(imagePath)) throw new FileNotFoundException("Wallpaper image was not found.", imagePath);
        var desktop = (IDesktopWallpaper)new DesktopWallpaper();
        try
        {
            desktop.GetMonitorRECT(monitorId, out var rect);
            var preparedPath = await WallpaperImageRenderer.PrepareAsync(imagePath, rect.Right - rect.Left, rect.Bottom - rect.Top, fit, token);
            // IDesktopWallpaper positioning is global. Every image is rendered to its monitor's exact
            // dimensions first, so a single neutral Stretch position preserves independent fit choices.
            desktop.SetPosition(DesktopWallpaperPosition.Stretch); desktop.SetWallpaper(monitorId, preparedPath);
        }
        catch (COMException ex) { throw new WallpaperItemException("Windows could not apply this wallpaper to the display.", ex); }
        finally { Marshal.FinalReleaseComObject(desktop); }
    }
    public Task<string?> GetWallpaperAsync(string monitorId, CancellationToken token = default)
    {
        var desktop = (IDesktopWallpaper)new DesktopWallpaper();
        try { return Task.FromResult<string?>(desktop.GetWallpaper(monitorId)); }
        catch { return Task.FromResult<string?>(null); }
        finally { Marshal.FinalReleaseComObject(desktop); }
    }
}

internal static class WallpaperImageRenderer
{
    private static readonly WallpaperCacheManager Cache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pane", "Cache"));

    public static Task<string> PrepareAsync(string sourcePath, int width, int height, WallpaperFit fit, CancellationToken token)
        => Task.Run(() => Prepare(sourcePath, width, height, fit, token), token);

    private static string Prepare(string sourcePath, int width, int height, WallpaperFit fit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var signature = $"{sourcePath}|{File.GetLastWriteTimeUtc(sourcePath).Ticks}|{width}x{height}|{fit}";
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))) + ".png";
        Cache.EnsureCacheDirectory(); var destination = Cache.GetCachePath(name);
        if (File.Exists(destination)) { Cache.Touch(destination); Cache.PruneIfNeeded(); return destination; }
        using var source = LoadSourceImage(sourcePath); using var output = new Bitmap(width, height, PixelFormat.Format24bppRgb); using var graphics = Graphics.FromImage(output);
        graphics.Clear(Color.Black); graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var target = GetTarget(source.Width, source.Height, width, height, fit); graphics.DrawImage(source, target); token.ThrowIfCancellationRequested();
        var temporary = Cache.CreateTemporaryPath(destination);
        try
        {
            output.Save(temporary, ImageFormat.Png); File.Move(temporary, destination, true);
            Cache.NotifyFileWritten(new FileInfo(destination).Length); return destination;
        }
        finally { Cache.TryDeleteTemporaryFile(temporary); }
    }

    private static Image LoadSourceImage(string sourcePath)
    {
        try { return Image.FromFile(sourcePath); }
        catch (ArgumentException ex) { throw new WallpaperItemException("The wallpaper image could not be decoded.", ex); }
        catch (ExternalException ex) { throw new WallpaperItemException("The wallpaper image could not be decoded.", ex); }
        // GDI+ reports some corrupt or unsupported images as OutOfMemoryException.
        catch (OutOfMemoryException ex) { throw new WallpaperItemException("The wallpaper image could not be decoded.", ex); }
    }

    private static Rectangle GetTarget(int imageWidth, int imageHeight, int monitorWidth, int monitorHeight, WallpaperFit fit)
    {
        if (fit == WallpaperFit.Stretch) return new Rectangle(0, 0, monitorWidth, monitorHeight);
        if (fit == WallpaperFit.Center) return new Rectangle((monitorWidth - imageWidth) / 2, (monitorHeight - imageHeight) / 2, imageWidth, imageHeight);
        var scale = fit == WallpaperFit.Fit
            ? Math.Min((double)monitorWidth / imageWidth, (double)monitorHeight / imageHeight)
            : Math.Max((double)monitorWidth / imageWidth, (double)monitorHeight / imageHeight);
        var width = Math.Max(1, (int)Math.Round(imageWidth * scale)); var height = Math.Max(1, (int)Math.Round(imageHeight * scale));
        return new Rectangle((monitorWidth - width) / 2, (monitorHeight - height) / 2, width, height);
    }
}

internal sealed class WallpaperTransitionService(IWallpaperService wallpaper) : IWallpaperTransitionService
{
    public Task ApplyAsync(MonitorInfo monitor, string imagePath, MonitorWallpaperProfile profile, CancellationToken token = default)
        => wallpaper.SetWallpaperAsync(monitor.Id, imagePath, profile.FitMode, token);
}

internal static class Native
{
    internal const int ENUM_CURRENT_SETTINGS = -1;
    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int HORZSIZE = 4;
    internal const int VERTSIZE = 6;
    internal const uint DISPLAYCONFIG_TARGET_NAME_EDID_IDS_VALID = 0x00000004;
    internal const uint DISPLAYCONFIG_ROTATION_IDENTITY = 1;
    internal const uint DISPLAYCONFIG_ROTATION_ROTATE90 = 2;
    internal const uint DISPLAYCONFIG_ROTATION_ROTATE180 = 3;
    internal const uint DISPLAYCONFIG_ROTATION_ROTATE270 = 4;
    internal const int DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER = -1;
    internal const int DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UNINITIALIZED = -2;
    internal const int DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6;
    internal const int DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    internal const int DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    internal const int DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = unchecked((int)0x80000000);
    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE mode);
    [DllImport("user32.dll")] internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, nint topologyId);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateDC(string driver, string device, string? output, nint initData);
    [DllImport("gdi32.dll")] internal static extern int GetDeviceCaps(nint deviceContext, int index);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint deviceContext);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx;
        public int outputTechnology;
        public uint rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct DISPLAYCONFIG_MODE_INFO_UNION { }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType, id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type, size;
        public LUID adapterId;
        public uint id;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        public static DISPLAYCONFIG_SOURCE_DEVICE_NAME Create(LUID adapterId, uint id) => new()
        {
            header = new() { type = 1, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = adapterId, id = id },
            viewGdiDeviceName = string.Empty
        };
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        public static DISPLAYCONFIG_TARGET_DEVICE_NAME Create(LUID adapterId, uint id) => new()
        {
            header = new() { type = 2, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = adapterId, id = id },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };
    }
}

[ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")] internal class DesktopWallpaper { }
[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
    void GetMonitorDevicePathAt(uint index, out nint monitorId); void GetMonitorDevicePathCount(out uint count); void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out Native.RECT rect);
    void SetBackgroundColor(uint color); void GetBackgroundColor(out uint color); void SetPosition(DesktopWallpaperPosition position); void GetPosition(out DesktopWallpaperPosition position);
}
internal enum DesktopWallpaperPosition { Center, Tile, Stretch, Fit, Fill, Span }
