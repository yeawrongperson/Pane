using System.Globalization;
using System.Text;

namespace Wallflow.Core;

public enum DisplayShellStyle { Auto, StandardFlat, UltrawideFlat, UltrawideCurved, LargeDisplay, Laptop }
public enum DisplayOrientation { Landscape, Portrait, LandscapeFlipped, PortraitFlipped }
public enum PhysicalSizeSource { None, EdidReported, GdiEstimated }
public enum PhysicalSizeConfidence { Unavailable, Estimated, EdidReported }
public enum DisplayStyleSource { Automatic, ManualOverride, SafeFallback }

public static class DisplayOrientationResolver
{
    public static DisplayOrientation Resolve(int? clockwiseRotationDegrees, int width, int height)
    {
        var portraitDimensions = height > width;
        return clockwiseRotationDegrees switch
        {
            90 => DisplayOrientation.Portrait,
            180 => portraitDimensions ? DisplayOrientation.PortraitFlipped : DisplayOrientation.LandscapeFlipped,
            270 => DisplayOrientation.PortraitFlipped,
            _ => portraitDimensions ? DisplayOrientation.Portrait : DisplayOrientation.Landscape
        };
    }
}

public sealed record PhysicalDisplaySize(int WidthMillimeters, int HeightMillimeters, double DiagonalInches);

public sealed record WmiMonitorPhysicalSize(
    string InstanceName,
    bool Active,
    int HorizontalImageSizeCentimeters,
    int VerticalImageSizeCentimeters);

public sealed record MatchedMonitorPhysicalSize(int WidthMillimeters, int HeightMillimeters);

public static class MonitorPhysicalSizeIdentityMatcher
{
    public static IReadOnlyDictionary<string, MatchedMonitorPhysicalSize> Match(
        IEnumerable<string> displayConfigMonitorDevicePaths,
        IEnumerable<WmiMonitorPhysicalSize> wmiMonitors)
    {
        ArgumentNullException.ThrowIfNull(displayConfigMonitorDevicePaths);
        ArgumentNullException.ThrowIfNull(wmiMonitors);
        var displayPaths = displayConfigMonitorDevicePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => (Path: path, Identity: NormalizeDisplayConfigPath(path)))
            .Where(item => item.Identity is not null)
            .ToArray();
        var uniqueDisplayPaths = displayPaths
            .GroupBy(item => item.Identity!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Path, StringComparer.OrdinalIgnoreCase);
        var uniqueWmiMonitors = wmiMonitors
            .Where(item => item.Active &&
                           item.HorizontalImageSizeCentimeters > 0 &&
                           item.VerticalImageSizeCentimeters > 0)
            .Select(item => (Monitor: item, Identity: NormalizeWmiInstanceName(item.InstanceName)))
            .Where(item => item.Identity is not null)
            .GroupBy(item => item.Identity!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Monitor, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, MatchedMonitorPhysicalSize>(StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, displayPath) in uniqueDisplayPaths)
        {
            if (!uniqueWmiMonitors.TryGetValue(identity, out var monitor)) continue;
            try
            {
                result[displayPath] = new(
                    checked(monitor.HorizontalImageSizeCentimeters * 10),
                    checked(monitor.VerticalImageSizeCentimeters * 10));
            }
            catch (OverflowException) { }
        }
        return result;
    }

    public static string? NormalizeDisplayConfigPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized[4..];
        var segments = normalized.Split('#');
        return segments.Length >= 3 && IsDisplayIdentity(segments[0], segments[1], segments[2])
            ? $"{segments[0]}\\{segments[1]}\\{segments[2]}"
            : null;
    }

    public static string? NormalizeWmiInstanceName(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        var segments = instanceName.Trim().Split('\\');
        if (segments.Length < 3) return null;
        var instance = System.Text.RegularExpressions.Regex.Replace(segments[2], @"_\d+$", string.Empty);
        return IsDisplayIdentity(segments[0], segments[1], instance)
            ? $"{segments[0]}\\{segments[1]}\\{instance}"
            : null;
    }

    private static bool IsDisplayIdentity(string root, string hardwareId, string instanceId)
        => string.Equals(root, "DISPLAY", StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(hardwareId) &&
           !string.IsNullOrWhiteSpace(instanceId);
}

public static class PhysicalDisplaySizeValidator
{
    public const int MinimumDimensionMillimeters = 50;
    public const int MaximumDimensionMillimeters = 3_000;
    public const double MinimumDiagonalInches = 4;
    public const double MaximumDiagonalInches = 120;
    public const double MaximumAspectRatioRelativeDifference = 0.20;
    private const double MillimetersPerInch = 25.4;

    public static PhysicalDisplaySize? Validate(int? widthMillimeters, int? heightMillimeters)
    {
        if (widthMillimeters is null || heightMillimeters is null) return null;
        if (widthMillimeters < MinimumDimensionMillimeters || heightMillimeters < MinimumDimensionMillimeters) return null;
        if (widthMillimeters > MaximumDimensionMillimeters || heightMillimeters > MaximumDimensionMillimeters) return null;
        var diagonal = Math.Sqrt(
            (double)widthMillimeters.Value * widthMillimeters.Value +
            (double)heightMillimeters.Value * heightMillimeters.Value) / MillimetersPerInch;
        if (!double.IsFinite(diagonal) || diagonal < MinimumDiagonalInches || diagonal > MaximumDiagonalInches) return null;
        return new(widthMillimeters.Value, heightMillimeters.Value, diagonal);
    }

    public static PhysicalDisplaySize? ValidateForClassification(
        int? widthMillimeters,
        int? heightMillimeters,
        int pixelWidth,
        int pixelHeight)
    {
        var size = Validate(widthMillimeters, heightMillimeters);
        if (size is null || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var physicalAspectRatio = (double)Math.Max(size.WidthMillimeters, size.HeightMillimeters) /
                                  Math.Min(size.WidthMillimeters, size.HeightMillimeters);
        var pixelAspectRatio = (double)Math.Max(pixelWidth, pixelHeight) / Math.Min(pixelWidth, pixelHeight);
        var relativeDifference = Math.Abs(physicalAspectRatio - pixelAspectRatio) / pixelAspectRatio;
        return relativeDifference <= MaximumAspectRatioRelativeDifference ? size : null;
    }
}

public sealed record DisplayStyleClassification(
    DisplayShellStyle ShellStyle,
    double AspectRatio,
    PhysicalDisplaySize? PhysicalSize,
    PhysicalSizeConfidence PhysicalSizeConfidence);

public static class DisplayStyleClassifier
{
    public const double UltrawideAspectRatioThreshold = 2.0;
    public const double LargeDisplayDiagonalInchesThreshold = 40.0;

    public static DisplayStyleClassification Classify(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var aspectRatio = CalculateAspectRatio(monitor.Width, monitor.Height);
        var physicalSize = PhysicalDisplaySizeValidator.ValidateForClassification(
            monitor.PhysicalWidthMillimeters,
            monitor.PhysicalHeightMillimeters,
            monitor.Width,
            monitor.Height);
        var confidence = physicalSize is null
            ? PhysicalSizeConfidence.Unavailable
            : monitor.PhysicalSizeSource == PhysicalSizeSource.EdidReported
                ? PhysicalSizeConfidence.EdidReported
                : PhysicalSizeConfidence.Estimated;
        var style = monitor.IsInternal == true
            ? DisplayShellStyle.Laptop
            : confidence == PhysicalSizeConfidence.EdidReported &&
              physicalSize?.DiagonalInches >= LargeDisplayDiagonalInchesThreshold
                ? DisplayShellStyle.LargeDisplay
                : aspectRatio >= UltrawideAspectRatioThreshold
                    ? DisplayShellStyle.UltrawideFlat
                    : DisplayShellStyle.StandardFlat;
        return new(style, aspectRatio, physicalSize, confidence);
    }

    public static double CalculateAspectRatio(int width, int height)
        => width <= 0 || height <= 0 ? 1 : (double)Math.Max(width, height) / Math.Min(width, height);
}

public sealed record MonitorVisualDescriptor(
    DisplayShellStyle ResolvedShellStyle,
    DisplayOrientation Orientation,
    double AspectRatio,
    double? PhysicalDiagonalInches,
    PhysicalSizeConfidence PhysicalSizeConfidence,
    DisplayStyleSource StyleSource,
    string? ModelName,
    bool? IsInternal);

public static class MonitorVisualResolver
{
    public static MonitorVisualDescriptor Resolve(MonitorInfo monitor, MonitorVisualPreference? preference = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var classification = DisplayStyleClassifier.Classify(monitor);
        var overrideStyle = preference?.StyleOverride ?? DisplayShellStyle.Auto;
        var resolvedStyle = overrideStyle == DisplayShellStyle.Auto ? classification.ShellStyle : overrideStyle;
        var source = overrideStyle == DisplayShellStyle.Auto ? DisplayStyleSource.Automatic : DisplayStyleSource.ManualOverride;
        if (resolvedStyle == DisplayShellStyle.Auto)
        {
            resolvedStyle = DisplayShellStyle.StandardFlat;
            source = DisplayStyleSource.SafeFallback;
        }
        return new(resolvedStyle, monitor.Orientation, classification.AspectRatio,
            classification.PhysicalSize?.DiagonalInches, classification.PhysicalSizeConfidence,
            source, monitor.ModelName ?? monitor.FriendlyName, monitor.IsInternal);
    }
}

public static class MonitorDiagnosticFormatter
{
    public static string Format(MonitorInfo monitor, MonitorVisualDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(descriptor);
        var builder = new StringBuilder();
        builder.AppendLine($"Name: {monitor.FriendlyName}");
        builder.AppendLine($"Model: {monitor.ModelName ?? "Unavailable"}");
        builder.AppendLine($"ID: {monitor.Id}");
        builder.AppendLine($"GDI device: {monitor.DeviceName}");
        builder.AppendLine($"Device path: {monitor.MonitorDevicePath ?? "Unavailable"}");
        builder.AppendLine($"Resolution: {monitor.Width} x {monitor.Height}");
        builder.AppendLine($"Orientation: {monitor.Orientation}");
        builder.AppendLine($"Refresh rate: {(monitor.RefreshRate > 0 ? monitor.RefreshRate + " Hz" : "Unavailable")}");
        builder.AppendLine($"Physical size: {FormatPhysicalSize(monitor)}");
        builder.AppendLine($"Manufacturer: {monitor.ManufacturerId ?? "Unavailable"}");
        builder.AppendLine($"Product: {monitor.ProductCode ?? "Unavailable"}");
        builder.AppendLine($"Internal: {FormatNullableBoolean(monitor.IsInternal)}");
        builder.AppendLine($"Auto style: {DisplayStyleClassifier.Classify(monitor).ShellStyle}");
        builder.AppendLine($"Resolved style: {descriptor.ResolvedShellStyle}");
        builder.Append($"Style source: {descriptor.StyleSource}");
        return builder.ToString();
    }

    private static string FormatPhysicalSize(MonitorInfo monitor)
    {
        var size = PhysicalDisplaySizeValidator.ValidateForClassification(
            monitor.PhysicalWidthMillimeters,
            monitor.PhysicalHeightMillimeters,
            monitor.Width,
            monitor.Height);
        return size is null ? "Unavailable" : string.Create(
            CultureInfo.InvariantCulture,
            $"{size.WidthMillimeters} x {size.HeightMillimeters} mm ({size.DiagonalInches:F1} in, {monitor.PhysicalSizeSource})");
    }

    private static string FormatNullableBoolean(bool? value)
        => value is null ? "Unavailable" : value.Value ? "Yes" : "No";
}
