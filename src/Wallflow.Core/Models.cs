namespace Wallflow.Core;

public enum WallpaperMode { Static, Slideshow }
public enum WallpaperFit { Fill, Fit, Stretch, Center, Span }
public enum TransitionKind { None, SoftFade, Crossfade, BlurDissolve, SlideLeft, SlideRight, SlideUp, ZoomFade }

public sealed record MonitorInfo(
    string Id, string DeviceName, string FriendlyName, int X, int Y,
    int Width, int Height, bool IsPrimary, int RefreshRate = 0,
    DisplayOrientation? ReportedOrientation = null,
    string? ModelName = null,
    string? ManufacturerId = null,
    string? ProductCode = null,
    string? MonitorDevicePath = null,
    int? PhysicalWidthMillimeters = null,
    int? PhysicalHeightMillimeters = null,
    PhysicalSizeSource PhysicalSizeSource = PhysicalSizeSource.None,
    bool? IsInternal = null)
{
    public DisplayOrientation Orientation => ReportedOrientation ??
        (Height > Width ? DisplayOrientation.Portrait : DisplayOrientation.Landscape);
    public bool IsPortrait => Orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;
    public string Resolution => $"{Width} × {Height}";
}

public sealed class MonitorWallpaperProfile
{
    public required string MonitorId { get; set; }
    public string? MonitorDevicePath { get; set; }
    public int DisplayX { get; set; }
    public int DisplayY { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public WallpaperMode Mode { get; set; } = WallpaperMode.Static;
    public string? StaticImagePath { get; set; }
    public string? SlideshowFolderPath { get; set; }
    public TimeSpan SlideshowInterval { get; set; } = SlideshowPolicy.DefaultInterval;
    public bool ShuffleEnabled { get; set; } = true;
    public bool LoopEnabled { get; set; } = true;
    public WallpaperFit FitMode { get; set; } = WallpaperFit.Fill;
    public TransitionKind Transition { get; set; } = TransitionKind.SoftFade;
    public int TransitionDurationMs { get; set; } = 700;
    public int CurrentSlideshowIndex { get; set; }
    public string? LastWallpaperPath { get; set; }
    public bool Enabled { get; set; } = true;
}
