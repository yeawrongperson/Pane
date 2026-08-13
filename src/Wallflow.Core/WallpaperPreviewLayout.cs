namespace Wallflow.Core;

public readonly record struct WallpaperPreviewViewport(double X, double Y, double Width, double Height);

public static class WallpaperPreviewLayout
{
    public static WallpaperPreviewViewport Calculate(
        double stageWidth,
        double stageHeight,
        double monitorWidth,
        double monitorHeight)
    {
        if (!IsPositiveFinite(stageWidth) || !IsPositiveFinite(stageHeight))
            return default;

        if (!IsPositiveFinite(monitorWidth) || !IsPositiveFinite(monitorHeight))
            return new(0, 0, stageWidth, stageHeight);

        var scale = Math.Min(stageWidth / monitorWidth, stageHeight / monitorHeight);
        var width = monitorWidth * scale;
        var height = monitorHeight * scale;
        return new((stageWidth - width) / 2, (stageHeight - height) / 2, width, height);
    }

    private static bool IsPositiveFinite(double value) => value > 0 && double.IsFinite(value);
}
