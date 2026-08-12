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
        Assert.IsTrue(result.Placements[1].Width > result.Placements[0].Width);
        Assert.IsTrue(result.Placements[0].Height > result.Placements[0].Width);
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
    public void Equal_physical_size_is_independent_of_1080p_4k_or_8k_resolution()
    {
        var descriptor = Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24);
        var result = Layout(
            Item("1080p", 0, 0, 1920, 1080, descriptor),
            Item("4k", 1920, 0, 3840, 2160, descriptor),
            Item("8k", 5760, 0, 7680, 4320, descriptor));

        var diagonals = result.Placements.Select(VisualDiagonal).ToArray();
        Assert.AreEqual(diagonals[0], diagonals[1], 1e-7);
        Assert.AreEqual(diagonals[0], diagonals[2], 1e-7);
    }

    [TestMethod]
    public void Equal_diagonal_landscape_and_portrait_have_comparable_magnitude_and_correct_shape()
    {
        var result = Layout(
            Item("landscape", 0, 0, 2560, 1440,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Landscape, 27)),
            Item("portrait", 2560, 0, 1440, 2560,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait, 27)));

        var landscape = result.Placements[0];
        var portrait = result.Placements[1];
        Assert.AreEqual(VisualDiagonal(landscape), VisualDiagonal(portrait), 1e-7);
        Assert.IsTrue(landscape.Width > landscape.Height);
        Assert.IsTrue(portrait.Height > portrait.Width);
    }

    [TestMethod]
    public void Ultrawide_shape_uses_aspect_ratio_without_pixel_width_becoming_magnitude()
    {
        var result = Layout(
            Item("3440", 0, 0, 3440, 1440, Descriptor(DisplayShellStyle.UltrawideFlat)),
            Item("3840", 3440, 0, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat)),
            Item("5120", 7280, 0, 5120, 1440, Descriptor(DisplayShellStyle.UltrawideFlat)));

        var placements = result.Placements.ToArray();
        Assert.AreEqual(3440d / 1440, placements[0].Width / placements[0].Height, 1e-7);
        Assert.AreEqual(3840d / 1600, placements[1].Width / placements[1].Height, 1e-7);
        Assert.AreEqual(5120d / 1440, placements[2].Width / placements[2].Height, 1e-7);
        Assert.AreEqual(VisualDiagonal(placements[0]), VisualDiagonal(placements[2]), 1e-7);
        Assert.IsTrue(placements[2].Width > placements[0].Width);
    }

    [TestMethod]
    public void Trusted_55_inch_display_is_larger_but_obeys_existing_factor_clamp()
    {
        var result = Layout(
            Item("24", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("55", 1920, 0, 3840, 2160, Descriptor(DisplayShellStyle.LargeDisplay, diagonal: 55)));
        var ratio = VisualDiagonal(result.Placements[1]) / VisualDiagonal(result.Placements[0]);

        Assert.AreEqual(Math.Sqrt(55d / 24), ratio, 1e-7);
        Assert.IsTrue(ratio <= MonitorTopologyLayout.MaximumTrustedSizeFactor);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Semantic_gap_compression_is_continuous_monotonic_and_bounded()
    {
        var threshold = MonitorTopologyLayout.UncompressedDesktopGap;
        Assert.AreEqual(threshold, MonitorTopologyLayout.CompressDesktopGap(threshold), 1e-7);
        Assert.IsTrue(MonitorTopologyLayout.CompressDesktopGap(1000) > threshold);
        Assert.IsTrue(MonitorTopologyLayout.CompressDesktopGap(8000) < MonitorTopologyLayout.CompressDesktopGap(80000));
        Assert.IsTrue(MonitorTopologyLayout.CompressDesktopGap(80000) < MonitorTopologyLayout.MaximumSemanticDesktopGap);
    }

    [TestMethod]
    public void Far_tv_distance_does_not_collapse_monitor_sizes()
    {
        var near = FarTvLayout(8000);
        var far = FarTvLayout(80000);
        var nearA = near.Placements.Single(item => item.Key == "a");
        var farA = far.Placements.Single(item => item.Key == "a");
        var nearTv = near.Placements.Single(item => item.Key == "tv");
        var farTv = far.Placements.Single(item => item.Key == "tv");

        Assert.IsTrue(nearTv.X > nearA.X);
        Assert.IsTrue(farTv.X > farA.X);
        Assert.IsTrue(Math.Abs(farA.Width / nearA.Width - 1) < 0.02,
            $"Shell width changed from {nearA.Width:F3} to {farA.Width:F3}.");
        var nearB = near.Placements.Single(item => item.Key == "b");
        var farB = far.Placements.Single(item => item.Key == "b");
        Assert.IsTrue(nearTv.X - (nearB.X + nearB.Width) < nearA.Width * 0.35);
        Assert.IsTrue(farTv.X - (farB.X + farB.Width) < farA.Width * 0.35);
        AssertInBounds(near, 500, 160);
        AssertInBounds(far, 500, 160);
    }

    [TestMethod]
    public void Vdd_regression_topology_is_readable_ordered_non_overlapping_and_deterministic()
    {
        var items = VddItems();
        var first = MonitorTopologyLayout.Calculate(items, 500, 139, 8, 12);
        var second = MonitorTopologyLayout.Calculate(items, 500, 139, 8, 12);
        var one = first.Placements.Single(item => item.Key == "1");
        var two = first.Placements.Single(item => item.Key == "2");
        var three = first.Placements.Single(item => item.Key == "3");
        var four = first.Placements.Single(item => item.Key == "4");

        Assert.IsTrue(one.Y < two.Y);
        Assert.IsTrue(one.X < three.X && two.X < three.X);
        Assert.IsTrue(four.X > three.X && four.Y < three.Y);
        Assert.IsTrue(three.Width > one.Width);
        Assert.IsTrue(four.Height > four.Width);
        Assert.IsTrue(first.Placements.Where(item => item.Key != "4").All(item => item.Width >= 50));
        AssertNoOverlaps(first);
        AssertInBounds(first, 500, 139);
        CollectionAssert.AreEqual(first.Placements.ToArray(), second.Placements.ToArray());
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
    public void Latest_vdd_three_monitor_row_preserves_center_alignment_and_order()
    {
        var result = Layout(
            Item("2", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("3", 1920, -260, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)),
            Item("1", 5760, 20, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)));
        var left = result.Placements.Single(item => item.Key == "2");
        var center = result.Placements.Single(item => item.Key == "3");
        var right = result.Placements.Single(item => item.Key == "1");

        Assert.IsTrue(left.X + left.Width <= center.X);
        Assert.IsTrue(center.X + center.Width <= right.X);
        Assert.AreEqual(VerticalCenter(left), VerticalCenter(center), 1);
        Assert.AreEqual(VerticalCenter(center), VerticalCenter(right), 1);
        AssertNoOverlaps(result);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Real_source_mode_geometry_places_vdd_asus_and_hp_in_one_adjacent_row()
    {
        var vddRaw = new DisplayGeometry(-7040, -382, 5120, 1440);
        var asusRaw = new DisplayGeometry(-1920, 22, 1920, 1080);
        var hpRaw = new DisplayGeometry(0, 0, 1920, 1080);
        Assert.AreEqual(0, asusRaw.X - vddRaw.Right);
        Assert.AreEqual(0, hpRaw.X - asusRaw.Right);

        var result = Layout(
            Item("VDD", vddRaw.X, vddRaw.Y, vddRaw.Width, vddRaw.Height,
                Descriptor(DisplayShellStyle.UltrawideFlat)),
            Item("ASUS", asusRaw.X, asusRaw.Y, asusRaw.Width, asusRaw.Height,
                Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("HP", hpRaw.X, hpRaw.Y, hpRaw.Width, hpRaw.Height,
                Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)));
        var vdd = result.Placements.Single(item => item.Key == "VDD");
        var asus = result.Placements.Single(item => item.Key == "ASUS");
        var hp = result.Placements.Single(item => item.Key == "HP");

        Assert.IsTrue(vdd.X + vdd.Width <= asus.X);
        Assert.IsTrue(asus.X + asus.Width <= hp.X);
        Assert.IsTrue(Math.Abs(Bottom(vdd) - Bottom(asus)) <= 1);
        Assert.IsTrue(Math.Abs(Bottom(asus) - Bottom(hp)) <= 2);
        AssertNoOverlaps(result);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Real_mixed_orientation_row_uses_portrait_footprints_without_false_overlap()
    {
        var result = Layout(
            Item("VDD3", -4080, -841, 1080, 1920,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait)),
            Item("ASUS", -3000, 3, 1920, 1080,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Landscape, 24)),
            Item("VDD4", -1080, -847, 1080, 1920,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait)),
            Item("HP", 0, 0, 1920, 1080,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Landscape, 24)));
        var vdd3 = result.Placements.Single(item => item.Key == "VDD3");
        var asus = result.Placements.Single(item => item.Key == "ASUS");
        var vdd4 = result.Placements.Single(item => item.Key == "VDD4");
        var hp = result.Placements.Single(item => item.Key == "HP");

        Assert.IsTrue(vdd3.Height > vdd3.Width);
        Assert.IsTrue(vdd4.Height > vdd4.Width);
        Assert.IsTrue(asus.Width > asus.Height);
        Assert.IsTrue(hp.Width > hp.Height);
        Assert.IsTrue(vdd3.X + vdd3.Width <= asus.X);
        Assert.IsTrue(asus.X + asus.Width <= vdd4.X);
        Assert.IsTrue(vdd4.X + vdd4.Width <= hp.X);
        AssertNoOverlaps(result);
        AssertInBounds(result, 500, 160);
    }

    [TestMethod]
    public void Mixed_physical_sizes_with_approximately_aligned_raw_centers_keep_visual_centers_aligned()
    {
        var result = Layout(
            Item("left", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("center", 1920, -250, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)),
            Item("right", 5760, -40, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)));
        var centers = result.Placements.Select(VerticalCenter).ToArray();

        Assert.IsTrue(centers.Max() - centers.Min() <= 1);
    }

    [TestMethod]
    public void Shared_raw_top_edges_remain_visually_top_aligned()
    {
        var result = Layout(
            Item("standard", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("wide", 1920, 0, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)));

        Assert.AreEqual(result.Placements[0].Y, result.Placements[1].Y, 1e-7);
    }

    [TestMethod]
    public void Shared_raw_bottom_edges_remain_visually_bottom_aligned()
    {
        var result = Layout(
            Item("standard", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("wide", 1920, -520, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)));

        Assert.AreEqual(Bottom(result.Placements[0]), Bottom(result.Placements[1]), 1e-7);
    }

    [TestMethod]
    public void Significant_portrait_offset_above_a_row_is_not_snapped_flat()
    {
        var result = Layout(
            Item("standard", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("wide", 1920, 0, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)),
            Item("portrait", 5000, -3000, 1440, 2560,
                Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait, 27)));
        var wide = result.Placements.Single(item => item.Key == "wide");
        var portrait = result.Placements.Single(item => item.Key == "portrait");

        Assert.IsTrue(Bottom(portrait) + 1 < wide.Y);
        AssertNoOverlaps(result);
    }

    [TestMethod]
    public void Adjacent_monitor_position_uses_empty_edge_gap_not_origin_distance()
    {
        var result = Layout(
            Item("wide", 0, 0, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)),
            Item("right", 3840, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)));
        var wide = result.Placements[0];
        var right = result.Placements[1];
        var renderedGap = right.X - (wide.X + wide.Width);

        Assert.IsTrue(renderedGap >= 0);
        Assert.IsTrue(renderedGap < wide.Width * 0.10);
    }

    [TestMethod]
    public void Five_displays_and_5120_ultrawide_fit_tiny_compact_viewport()
    {
        var result = MonitorTopologyLayout.Calculate([
            Item("a", -1080, 0, 1080, 1920), Item("b", 0, 0, 1920, 1080),
            Item("c", 1920, 0, 5120, 1440, Descriptor(DisplayShellStyle.UltrawideFlat)),
            Item("d", 7040, -1080, 1920, 1080), Item("e", 7040, 0, 1920, 1080)], 112, 48, 3, 2);
        AssertInBounds(result, 112, 48);
        Assert.IsTrue(result.Placements.Any(item => item.Width > item.Height));
        Assert.IsTrue(result.Placements.Any(item => item.Height > item.Width));
    }

    [TestMethod]
    public void Vdd_topology_fits_compact_112_by_48_viewport()
    {
        var result = MonitorTopologyLayout.Calculate(VddItems(), 112, 48, 3, 2);
        AssertInBounds(result, 112, 48);
        AssertNoOverlaps(result);
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
    private static MonitorTopologyResult FarTvLayout(double tvX)
        => Layout(
            Item("a", 0, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("b", 1920, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
            Item("tv", tvX, 0, 3840, 2160, Descriptor(DisplayShellStyle.LargeDisplay, diagonal: 55)));
    private static MonitorTopologyItem[] VddItems() =>
    [
        Item("1", -8000, 0, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
        Item("2", -8000, 1080, 1920, 1080, Descriptor(DisplayShellStyle.StandardFlat, diagonal: 24)),
        Item("3", 0, 0, 3840, 1600, Descriptor(DisplayShellStyle.UltrawideFlat, diagonal: 38)),
        Item("4", 4000, -4000, 1440, 2560,
            Descriptor(DisplayShellStyle.StandardFlat, DisplayOrientation.Portrait, 27))
    ];
    private static MonitorTopologyItem Item(string key, double x, double y, double w, double h, MonitorVisualDescriptor? descriptor = null)
        => new(key, x, y, w, h, descriptor ?? Descriptor(DisplayShellStyle.StandardFlat), true);
    private static MonitorVisualDescriptor Descriptor(DisplayShellStyle style, DisplayOrientation orientation = DisplayOrientation.Landscape, double? diagonal = null)
        => new(style, orientation, 16d / 9, diagonal, diagonal.HasValue ? PhysicalSizeConfidence.EdidReported : PhysicalSizeConfidence.Unavailable, DisplayStyleSource.Automatic, null, false);
    private static double VisualDiagonal(MonitorTopologyPlacement placement)
        => Math.Sqrt(placement.Width * placement.Width + placement.Height * placement.Height);
    private static double VerticalCenter(MonitorTopologyPlacement placement) => placement.Y + placement.Height / 2;
    private static double Bottom(MonitorTopologyPlacement placement) => placement.Y + placement.Height;
    private static void AssertNoOverlaps(MonitorTopologyResult result)
    {
        var placements = result.Placements.ToArray();
        for (var first = 0; first < placements.Length; first++)
        for (var second = first + 1; second < placements.Length; second++)
        {
            var a = placements[first];
            var b = placements[second];
            var overlaps = a.X < b.X + b.Width - 1e-7 && b.X < a.X + a.Width - 1e-7 &&
                           a.Y < b.Y + b.Height - 1e-7 && b.Y < a.Y + a.Height - 1e-7;
            Assert.IsFalse(overlaps, $"{a.Key} overlaps {b.Key}.");
        }
    }
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
