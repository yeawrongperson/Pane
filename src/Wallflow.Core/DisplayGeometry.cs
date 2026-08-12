namespace Wallflow.Core;

public sealed record DisplayGeometry(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;

    public bool IsValid => Width > 0 && Height > 0 &&
                           Right is >= int.MinValue and <= int.MaxValue &&
                           Bottom is >= int.MinValue and <= int.MaxValue;
}

public enum DisplayGeometrySource
{
    DisplayConfigSourceMode,
    CurrentDevMode,
    MonitorInfo
}

public sealed record ResolvedDisplayGeometry(DisplayGeometry Geometry, DisplayGeometrySource Source);

public enum DisplayConfigRotation
{
    Identity,
    Rotate90,
    Rotate180,
    Rotate270
}

public static class DisplayConfigSourceModeFootprint
{
    public static DisplayGeometry FromSourceMode(DisplayGeometry sourceMode, DisplayConfigRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(sourceMode);
        return rotation is DisplayConfigRotation.Rotate90 or DisplayConfigRotation.Rotate270
            ? new(sourceMode.X, sourceMode.Y, sourceMode.Height, sourceMode.Width)
            : sourceMode;
    }
}

public static class DisplayGeometryResolver
{
    public static ResolvedDisplayGeometry Resolve(
        DisplayGeometry monitorInfo,
        DisplayGeometry? displayConfigSourceMode,
        DisplayGeometry? currentDevMode)
    {
        ArgumentNullException.ThrowIfNull(monitorInfo);
        if (displayConfigSourceMode?.IsValid == true)
            return new(displayConfigSourceMode, DisplayGeometrySource.DisplayConfigSourceMode);
        if (currentDevMode?.IsValid == true)
            return new(currentDevMode, DisplayGeometrySource.CurrentDevMode);
        return new(monitorInfo, DisplayGeometrySource.MonitorInfo);
    }
}

public static class DisplayConfigSourceModeIndex
{
    public const uint Invalid = 0xFFFFFFFF;

    public static uint Decode(uint modeInfoIndexUnion, bool supportsVirtualMode)
        => supportsVirtualMode ? modeInfoIndexUnion >> 16 : modeInfoIndexUnion;

    public static bool TryDecode(
        uint modeInfoIndexUnion,
        bool supportsVirtualMode,
        uint modeCount,
        out uint sourceModeIndex)
    {
        sourceModeIndex = Decode(modeInfoIndexUnion, supportsVirtualMode);
        return sourceModeIndex != Invalid && sourceModeIndex < modeCount;
    }
}
