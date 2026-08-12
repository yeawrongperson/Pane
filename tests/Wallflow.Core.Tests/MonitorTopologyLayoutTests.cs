using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class MonitorTopologyLayoutTests
{
    [TestMethod] public void Single_monitor_fits() => AssertInBounds(Layout(Item("a", 0, 0, 1920, 1080)), 500, 160);

    [TestMethod]
    public void Dual_and_negative_coordinates_preserve_left_right_order()
    {
        var result = Layout(Item("left", -1920, 0, 1920, 1080), Item("right", 0, 0, 1920, 1080));
        Assert.IsTrue(result.Placements[0].X < result.Placements[1].X);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Above_below_and_vertical_stack_preserve_order()
    {
        var result = Layout(Item("top", 0, -2160, 3840, 2160), Item("middle", 0, 0, 1920, 1080), Item("bottom", 0, 1080, 1920, 1080));
        Assert.IsTrue(result.Placements[0].Y < result.Placements[1].Y);
        Assert.IsTrue(result.Placements[1].Y < result.Placements[2].Y);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Portrait_curved_ultrawide_portrait_uses_trusted_relative_size()
    {
        var portrait = Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait, 24);
        var curved = Descriptor(DisplayShellStyle.UltrawideCurved, DisplayOrientation.Landscape, 38);
        var result = Layout(
            Item("left", -1080, 0, 1080, 1920, portrait),
            Item("center", 0, 160, 3840, 1600, curved),
            Item("right", 3840, 0, 1080, 1920, portrait));
        Assert.IsTrue(result.Placements[1].Width > result.Placements[0].Width * 3.5);
        Assert.IsTrue(result.Placements[0].X + result.Placements[0].Width <= result.Placements[1].X);
        Assert.IsTrue(result.Placements[1].X + result.Placements[1].Width <= result.Placements[2].X);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Trusted_48_inch_panel_is_meaningfully_larger_than_24_inch_panel()
    {
        var result = Layout(
            Item("small", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("large", 1920, 0, 1920, 1080, Descriptor(DisplayShellStyle.LargeDisplay, diagonal: 48)));
        Assert.IsTrue(result.Placements[1].Width > result.Placements[0].Width * 1.4);
    }

    [TestMethod]
    public void Trusted_size_factor_is_clamped_and_estimated_size_is_ignored()
    {
        Assert.AreEqual(MonitorTopologyLayout.MaximumTrustedSizeFactor,
            MonitorTopologyLayout.PhysicalSizeFactor(Descriptor(DisplayShellStyle.LargeDisplay, diagonal: 10000)));
        var estimated = Descriptor(DisplayShellStyle.LargeDisplay, diagonal: 90) with { PhysicalSizeConfidence = PhysicalSizeConfidence.Estimated };
        Assert.AreEqual(1, MonitorTopologyLayout.PhysicalSizeFactor(estimated));
    }

    [TestMethod]
    public void Five_displays_and_5120_ultrawide_fit_tiny_compact_viewport()
    {
        var result = MonitorTopologyLayout.Calculate([
            Item("a", -1080, 0, 1080, 1920), Item("b", 0, 0, 1920, 1080),
            Item("c", 1920, 0, 5120, 1440, Descriptor(DisplayShellStyle.UltrawideFlat)),
            Item("d", 7040, -1080, 1920, 1080), Item("e", 7040, 0, 1920, 1080)], 112, 48, 3, 2);
        AssertInBounds(result, 112, 48);
    }

    [TestMethod]
    public void Pathological_coordinates_and_invalid_dimensions_are_finite_and_deterministic()
    {
        var items = new[] { Item("a", -1e200, double.NaN, 0, -1), Item("b", 1e200, double.PositiveInfinity, 1920, 1080) };
        var first = MonitorTopologyLayout.Calculate(items, 112, 48, 3, 2);
        var second = MonitorTopologyLayout.Calculate(items, 112, 48, 3, 2);
        AssertInBounds(first, 112, 48);
        CollectionAssert.AreEqual(first.Placements.ToArray(), second.Placements.ToArray());
    }

    private static MonitorTopologyResult Layout(params MonitorTopologyItem[] items)
        => MonitorTopologyLayout.Calculate(items, 500, 160, 8, 12);
    private static MonitorTopologyItem Item(string key, double x, double y, double w, double h, MonitorVisualDescriptor? descriptor = null)
        => new(key, x, y, w, h, descriptor ?? Descriptor(DisplayShellStyle.StandardFlat), true);
    private static MonitorVisualDescriptor Descriptor(DisplayShellStyle style, DisplayOrientation orientation = DisplayOrientation.Landscape, double? diagonal = null)
        => new(style, orientation, 16d / 9, diagonal, diagonal.HasValue ? PhysicalSizeConfidence.EdidReported : PhysicalSizeConfidence.Unavailable, DisplayStyleSource.Automatic, null, false);
    private static void AssertInBounds(MonitorTopologyResult result, double width, double height)
    {
        foreach (var item in result.Placements)
        {
            Assert.IsTrue(double.IsFinite(item.X + item.Y + item.Width + item.Height));
            Assert.IsTrue(item.X >= -1e-7 && item.Y >= -1e-7);
            Assert.IsTrue(item.X + item.Width <= width + 1e-7);
            Assert.IsTrue(item.Y + item.Height <= height + 1e-7);
        }
    }
}

[TestClass]
public sealed class SavedMonitorVisualResolverTests
{
    [TestMethod]
    public void Saved_preference_preserves_portrait_manual_curve_without_mutation()
    {
        var profile = Profile();
        var preference = new MonitorVisualPreference { MonitorId = "saved", StyleOverride = DisplayShellStyle.UltrawideCurved,
            LastKnownWidth = 3840, LastKnownHeight = 1600, LastKnownOrientation = DisplayOrientation.PortraitFlipped };
        var before = Snapshot(preference);
        var descriptor = SavedMonitorVisualResolver.Resolve(profile, [preference]);
        Assert.AreEqual(DisplayShellStyle.UltrawideCurved, descriptor.ResolvedShellStyle);
        Assert.AreEqual(DisplayOrientation.PortraitFlipped, descriptor.Orientation);
        Assert.AreEqual(before, Snapshot(preference));
    }

    [TestMethod]
    public void No_preference_is_neutral_standard_fallback()
    {
        var profile = Profile();
        profile.DisplayWidth = 5120;
        profile.DisplayHeight = 1440;
        var descriptor = SavedMonitorVisualResolver.Resolve(profile, []);
        Assert.AreEqual(DisplayShellStyle.StandardFlat, descriptor.ResolvedShellStyle);
        Assert.AreEqual(DisplayStyleSource.SafeFallback, descriptor.StyleSource);
    }

    private static MonitorWallpaperProfile Profile() => new() { MonitorId = "saved", DisplayWidth = 1080, DisplayHeight = 1920 };
    private static string Snapshot(MonitorVisualPreference value) => $"{value.MonitorId}|{value.StyleOverride}|{value.LastKnownWidth}|{value.LastKnownHeight}|{value.LastKnownOrientation}";
}
