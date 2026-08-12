namespace Wallflow.Core;

public sealed record WallpaperTargetCandidate(
    string Id,
    DisplayGeometry? Geometry,
    bool IsActive);

public enum WallpaperTargetMatchKind
{
    TargetDevicePath,
    SourceGeometry,
    MonitorInfoGeometry,
    Unresolved,
    Ambiguous
}

public sealed record WallpaperTargetResolution(
    string? WallpaperId,
    WallpaperTargetMatchKind MatchKind)
{
    public bool IsResolved => !string.IsNullOrWhiteSpace(WallpaperId);
}

public static class WallpaperTargetResolver
{
    public static WallpaperTargetResolution Resolve(
        string? displayConfigTargetDevicePath,
        DisplayGeometry? sourceGeometry,
        DisplayGeometry monitorInfoGeometry,
        IEnumerable<WallpaperTargetCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(monitorInfoGeometry);
        ArgumentNullException.ThrowIfNull(candidates);
        var active = candidates.Where(candidate => candidate.IsActive && candidate.Geometry?.IsValid == true).ToArray();

        if (!string.IsNullOrWhiteSpace(displayConfigTargetDevicePath))
        {
            var byPath = active.Where(candidate => string.Equals(
                candidate.Id,
                displayConfigTargetDevicePath,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byPath.Length == 1) return new(byPath[0].Id, WallpaperTargetMatchKind.TargetDevicePath);
            if (byPath.Length > 1) return new(null, WallpaperTargetMatchKind.Ambiguous);
        }

        if (sourceGeometry?.IsValid == true)
        {
            var bySourceGeometry = active.Where(candidate => candidate.Geometry == sourceGeometry).ToArray();
            if (bySourceGeometry.Length == 1) return new(bySourceGeometry[0].Id, WallpaperTargetMatchKind.SourceGeometry);
            if (bySourceGeometry.Length > 1) return new(null, WallpaperTargetMatchKind.Ambiguous);
        }

        var byMonitorInfoGeometry = active.Where(candidate => candidate.Geometry == monitorInfoGeometry).ToArray();
        if (byMonitorInfoGeometry.Length == 1)
            return new(byMonitorInfoGeometry[0].Id, WallpaperTargetMatchKind.MonitorInfoGeometry);
        return new(null, byMonitorInfoGeometry.Length > 1
            ? WallpaperTargetMatchKind.Ambiguous
            : WallpaperTargetMatchKind.Unresolved);
    }
}
