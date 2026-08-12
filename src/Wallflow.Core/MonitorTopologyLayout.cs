namespace Wallflow.Core;

public sealed record MonitorTopologyItem(
    string Key,
    double DesktopX,
    double DesktopY,
    double DisplayWidth,
    double DisplayHeight,
    MonitorVisualDescriptor Descriptor,
    bool IsConnected = true);

public sealed record MonitorTopologyPlacement(
    string Key,
    double X,
    double Y,
    double Width,
    double Height,
    double Scale,
    bool IsConnected);

public sealed record MonitorTopologyResult(
    IReadOnlyList<MonitorTopologyPlacement> Placements,
    double ContentWidth,
    double ContentHeight,
    double Scale);

/// <summary>Pure, deterministic desktop-topology projection shared by all Pane renderers.</summary>
public static class MonitorTopologyLayout
{
    public const double FallbackLandscapeWidth = 1920;
    public const double FallbackLandscapeHeight = 1080;
    public const double TrustedDiagonalBaselineInches = 24;
    public const double MinimumTrustedSizeFactor = 0.80;
    public const double MaximumTrustedSizeFactor = 1.60;

    public static MonitorTopologyResult Calculate(
        IEnumerable<MonitorTopologyItem> source,
        double viewportWidth,
        double viewportHeight,
        double padding = 0,
        double gap = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source.Select(Normalize).ToArray();
        var safeWidth = FinitePositive(viewportWidth, 1);
        var safeHeight = FinitePositive(viewportHeight, 1);
        var safePadding = ClampFinite(padding, 0, Math.Min(safeWidth, safeHeight) / 2);
        var safeGap = ClampFinite(gap, 0, Math.Min(safeWidth, safeHeight) / 2);
        if (items.Length == 0) return new([], 0, 0, 1);

        var xRanks = items.Select(item => item.X).Distinct().Order().ToArray();
        var yRanks = items.Select(item => item.Y).Distinct().Order().ToArray();
        var minX = items.Min(item => item.X);
        var minY = items.Min(item => item.Y);
        var rankOffsets = new Dictionary<double, double>();
        var previousRight = double.NegativeInfinity;
        foreach (var rank in xRanks)
        {
            var desired = rank - minX;
            var offset = double.IsFinite(previousRight) ? Math.Max(0, previousRight - desired) : 0;
            rankOffsets[rank] = offset;
            previousRight = Math.Max(previousRight, items.Where(item => item.X == rank).Max(item => desired + offset + item.Width));
        }
        var raw = items.Select(item => new
        {
            Item = item,
            X = item.X - minX + rankOffsets[item.X],
            Y = item.Y - minY,
            XRank = Array.IndexOf(xRanks, item.X),
            YRank = Array.IndexOf(yRanks, item.Y)
        }).ToArray();
        var contentWidth = raw.Max(item => item.X + item.Item.Width);
        var contentHeight = raw.Max(item => item.Y + item.Item.Height);
        var availableWidth = Math.Max(1, safeWidth - safePadding * 2);
        var availableHeight = Math.Max(1, safeHeight - safePadding * 2);
        var horizontalGaps = Math.Max(0, xRanks.Length - 1) * safeGap;
        var verticalGaps = Math.Max(0, yRanks.Length - 1) * safeGap;
        var scale = Math.Min(Math.Max(1, availableWidth - horizontalGaps) / Math.Max(1, contentWidth),
            Math.Max(1, availableHeight - verticalGaps) / Math.Max(1, contentHeight));
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var renderedWidth = contentWidth * scale + horizontalGaps;
        var renderedHeight = contentHeight * scale + verticalGaps;
        var offsetX = safePadding + (availableWidth - renderedWidth) / 2;
        var offsetY = safePadding + (availableHeight - renderedHeight) / 2;
        var placements = raw.Select(item => new MonitorTopologyPlacement(
            item.Item.Key,
            offsetX + item.X * scale + item.XRank * safeGap,
            offsetY + item.Y * scale + item.YRank * safeGap,
            Math.Max(double.Epsilon, item.Item.Width * scale),
            Math.Max(double.Epsilon, item.Item.Height * scale),
            scale,
            item.Item.Connected)).ToArray();
        return new(placements, renderedWidth, renderedHeight, scale);
    }

    public static double PhysicalSizeFactor(MonitorVisualDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.PhysicalSizeConfidence != PhysicalSizeConfidence.EdidReported ||
            descriptor.PhysicalDiagonalInches is not > 0 ||
            !double.IsFinite(descriptor.PhysicalDiagonalInches.Value)) return 1;
        return Math.Clamp(
            Math.Sqrt(descriptor.PhysicalDiagonalInches.Value / TrustedDiagonalBaselineInches),
            MinimumTrustedSizeFactor,
            MaximumTrustedSizeFactor);
    }

    private static (string Key, double X, double Y, double Width, double Height, bool Connected) Normalize(MonitorTopologyItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Descriptor);
        var portrait = item.Descriptor.Orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped;
        var fallbackWidth = portrait ? FallbackLandscapeHeight : FallbackLandscapeWidth;
        var fallbackHeight = portrait ? FallbackLandscapeWidth : FallbackLandscapeHeight;
        var width = FinitePositive(item.DisplayWidth, fallbackWidth);
        var height = FinitePositive(item.DisplayHeight, fallbackHeight);
        var factor = PhysicalSizeFactor(item.Descriptor);
        return (item.Key ?? string.Empty, Finite(item.DesktopX), Finite(item.DesktopY), width * factor, height * factor, item.IsConnected);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static double FinitePositive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private static double ClampFinite(double value, double minimum, double maximum)
        => Math.Clamp(double.IsFinite(value) ? value : minimum, minimum, maximum);
}

public static class SavedMonitorVisualResolver
{
    public static MonitorVisualDescriptor Resolve(
        MonitorWallpaperProfile profile,
        IEnumerable<MonitorVisualPreference> preferences)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);
        var all = preferences.ToArray();
        var preference = all.FirstOrDefault(candidate =>
            string.Equals(candidate.MonitorId, profile.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (preference is null && !string.IsNullOrWhiteSpace(profile.MonitorDevicePath))
        {
            var matches = all.Where(candidate => !string.IsNullOrWhiteSpace(candidate.MonitorDevicePath) &&
                string.Equals(candidate.MonitorDevicePath, profile.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) preference = matches[0];
        }

        var width = preference?.LastKnownWidth > 0 ? preference.LastKnownWidth : profile.DisplayWidth;
        var height = preference?.LastKnownHeight > 0 ? preference.LastKnownHeight : profile.DisplayHeight;
        if (width <= 0 || height <= 0)
        {
            width = (int)MonitorTopologyLayout.FallbackLandscapeWidth;
            height = (int)MonitorTopologyLayout.FallbackLandscapeHeight;
        }
        if (preference is null)
        {
            var orientation = height > width ? DisplayOrientation.Portrait : DisplayOrientation.Landscape;
            return new(DisplayShellStyle.StandardFlat, orientation,
                DisplayStyleClassifier.CalculateAspectRatio(width, height), null,
                PhysicalSizeConfidence.Unavailable, DisplayStyleSource.SafeFallback, null, null);
        }

        var monitor = new MonitorInfo(profile.MonitorId, profile.MonitorDevicePath ?? profile.MonitorId,
            preference.LastKnownModelName ?? "Saved display", profile.DisplayX, profile.DisplayY, width, height, false,
            ReportedOrientation: preference.LastKnownOrientation,
            ModelName: preference.LastKnownModelName,
            PhysicalWidthMillimeters: preference.LastKnownPhysicalWidthMillimeters,
            PhysicalHeightMillimeters: preference.LastKnownPhysicalHeightMillimeters,
            PhysicalSizeSource: preference.LastKnownPhysicalSizeSource,
            IsInternal: preference.LastKnownIsInternal);
        return MonitorVisualResolver.Resolve(monitor, preference);
    }
}
