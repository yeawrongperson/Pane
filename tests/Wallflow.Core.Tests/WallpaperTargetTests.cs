using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class WallpaperTargetTests
{
    private const string VddPath = @"\\?\DISPLAY#MTT1337#1&28a6823a&2&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private static readonly DisplayGeometry VddGdi = new(-5120, -382, 2926, 823);
    private static readonly DisplayGeometry VddSource = new(-5120, -382, 5120, 1440);

    [TestMethod]
    public void Real_vdd_target_path_wins_and_no_gdi_fallback_is_manufactured()
    {
        var result = Resolve(VddPath, VddSource, VddGdi,
            new WallpaperTargetCandidate(VddPath, VddSource, true));

        Assert.AreEqual(WallpaperTargetMatchKind.TargetDevicePath, result.MatchKind);
        Assert.AreEqual(VddPath, result.WallpaperId);
        Assert.AreNotEqual(@"\\.\DISPLAY15", result.WallpaperId);
    }

    [TestMethod]
    public void Exact_path_match_wins_even_when_rectangles_do_not_match()
    {
        var result = Resolve(VddPath, VddSource, VddGdi,
            new(VddPath.ToLowerInvariant(), new(100, 200, 300, 400), true),
            new("source-rect", VddSource, true));

        Assert.AreEqual(WallpaperTargetMatchKind.TargetDevicePath, result.MatchKind);
        Assert.AreEqual(VddPath.ToLowerInvariant(), result.WallpaperId);
    }

    [TestMethod]
    public void Portrait_footprint_correction_does_not_change_target_path_identity_matching()
    {
        var portraitFootprint = new DisplayGeometry(-4080, -841, 1080, 1920);
        var result = Resolve(VddPath, portraitFootprint, portraitFootprint,
            new WallpaperTargetCandidate(VddPath, portraitFootprint, true));

        Assert.AreEqual(WallpaperTargetMatchKind.TargetDevicePath, result.MatchKind);
        Assert.AreEqual(VddPath, result.WallpaperId);
    }

    [TestMethod]
    public void Unique_source_rectangle_is_second_priority_fallback()
    {
        var result = Resolve("missing-path", VddSource, VddGdi,
            new WallpaperTargetCandidate("source-rect", VddSource, true));

        Assert.AreEqual(WallpaperTargetMatchKind.SourceGeometry, result.MatchKind);
        Assert.AreEqual("source-rect", result.WallpaperId);
    }

    [TestMethod]
    public void Unique_monitor_info_rectangle_is_legacy_fallback()
    {
        var result = Resolve("missing-path", new(1, 2, 3, 4), VddGdi,
            new WallpaperTargetCandidate("legacy-gdi", VddGdi, true));

        Assert.AreEqual(WallpaperTargetMatchKind.MonitorInfoGeometry, result.MatchKind);
        Assert.AreEqual("legacy-gdi", result.WallpaperId);
    }

    [TestMethod]
    public void Detached_target_does_not_participate_even_when_path_matches()
    {
        var result = Resolve(VddPath, VddSource, VddGdi,
            new WallpaperTargetCandidate(VddPath, null, false));

        Assert.AreEqual(WallpaperTargetMatchKind.Unresolved, result.MatchKind);
        Assert.IsNull(result.WallpaperId);
    }

    [TestMethod]
    public void Ambiguous_path_does_not_fall_through_or_guess()
    {
        var result = Resolve(VddPath, VddSource, VddGdi,
            new(VddPath, VddSource, true),
            new(VddPath.ToLowerInvariant(), VddSource, true));

        Assert.AreEqual(WallpaperTargetMatchKind.Ambiguous, result.MatchKind);
        Assert.IsNull(result.WallpaperId);
    }

    [TestMethod]
    public void Ambiguous_source_rectangle_does_not_fall_through_or_guess()
    {
        var result = Resolve(null, VddSource, VddGdi,
            new("one", VddSource, true),
            new("two", VddSource, true));

        Assert.AreEqual(WallpaperTargetMatchKind.Ambiguous, result.MatchKind);
        Assert.IsNull(result.WallpaperId);
    }

    [TestMethod]
    public void Ambiguous_monitor_info_rectangle_does_not_guess()
    {
        var result = Resolve(null, null, VddGdi,
            new("one", VddGdi, true),
            new("two", VddGdi, true));

        Assert.AreEqual(WallpaperTargetMatchKind.Ambiguous, result.MatchKind);
        Assert.IsNull(result.WallpaperId);
    }

    [TestMethod]
    public void No_target_returns_safe_unresolved_result_without_gdi_id()
    {
        var result = Resolve(null, VddSource, VddGdi);

        Assert.AreEqual(WallpaperTargetMatchKind.Unresolved, result.MatchKind);
        Assert.IsNull(result.WallpaperId);
    }

    private static WallpaperTargetResolution Resolve(
        string? path,
        DisplayGeometry? source,
        DisplayGeometry gdi,
        params WallpaperTargetCandidate[] candidates)
        => WallpaperTargetResolver.Resolve(path, source, gdi, candidates);
}
