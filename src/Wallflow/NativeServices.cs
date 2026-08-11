using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using Wallflow.Core;

namespace Wallflow;

internal sealed class WindowsMonitorService : IMonitorService
{
    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken token = default)
    {
        var displays = new List<MonitorInfo>(); var index = 0;
        var wallpaperTargets = GetWallpaperTargets();
        Native.EnumDisplayMonitors(nint.Zero, nint.Zero, (handle, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            var mode = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>(), dmDeviceName = string.Empty, dmFormName = string.Empty };
            var hasCurrentMode = Native.EnumDisplaySettings(info.szDevice, Native.ENUM_CURRENT_SETTINGS, ref mode);
            var number = ++index; var width = info.rcMonitor.Right - info.rcMonitor.Left; var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var wallpaperId = wallpaperTargets.FirstOrDefault(target => target.Rect.Left == info.rcMonitor.Left && target.Rect.Top == info.rcMonitor.Top && target.Rect.Right == info.rcMonitor.Right && target.Rect.Bottom == info.rcMonitor.Bottom).Id;
            displays.Add(new(wallpaperId ?? info.szDevice, info.szDevice, $"Display {number}", info.rcMonitor.Left, info.rcMonitor.Top,
                width, height, (info.dwFlags & 1) != 0, hasCurrentMode && mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0));
            return true;
        }, nint.Zero);
        return Task.FromResult<IReadOnlyList<MonitorInfo>>(displays);
    }

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
        catch (COMException ex) { throw new InvalidOperationException("Windows could not apply the wallpaper to this display.", ex); }
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
        using var source = Image.FromFile(sourcePath); using var output = new Bitmap(width, height, PixelFormat.Format24bppRgb); using var graphics = Graphics.FromImage(output);
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
    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE mode);
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
