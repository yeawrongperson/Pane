using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class DisplayGeometryTests
{
    private static readonly DisplayGeometry Gdi = new(-7040, -382, 2926, 823);
    private static readonly DisplayGeometry DevMode = new(-7040, -382, 5120, 1440);
    private static readonly DisplayGeometry DisplayConfig = new(-7040, -382, 5120, 1440);

    [TestMethod]
    public void All_sources_agree_and_display_config_wins_without_changing_geometry()
    {
        var geometry = new DisplayGeometry(0, 0, 1920, 1080);
        var result = DisplayGeometryResolver.Resolve(geometry, geometry, geometry);

        Assert.AreEqual(DisplayGeometrySource.DisplayConfigSourceMode, result.Source);
        Assert.AreEqual(geometry, result.Geometry);
    }

    [TestMethod]
    public void Vdd_source_mode_replaces_scaled_monitor_info_as_one_complete_rectangle()
    {
        var result = DisplayGeometryResolver.Resolve(Gdi, DisplayConfig, DevMode);

        Assert.AreEqual(DisplayGeometrySource.DisplayConfigSourceMode, result.Source);
        Assert.AreEqual(DisplayConfig, result.Geometry);
        Assert.AreEqual(-1920, result.Geometry.Right);
    }

    [TestMethod]
    public void Missing_display_config_uses_complete_current_devmode_rectangle()
    {
        var result = DisplayGeometryResolver.Resolve(Gdi, null, DevMode);

        Assert.AreEqual(DisplayGeometrySource.CurrentDevMode, result.Source);
        Assert.AreEqual(DevMode, result.Geometry);
    }

    [TestMethod]
    public void Invalid_display_config_uses_complete_current_devmode_rectangle()
    {
        var invalid = new DisplayGeometry(123, 456, 0, 1440);
        var result = DisplayGeometryResolver.Resolve(Gdi, invalid, DevMode);

        Assert.AreEqual(DisplayGeometrySource.CurrentDevMode, result.Source);
        Assert.AreEqual(DevMode, result.Geometry);
    }

    [TestMethod]
    public void Missing_display_config_and_devmode_use_monitor_info_rectangle()
    {
        var result = DisplayGeometryResolver.Resolve(Gdi, null, null);

        Assert.AreEqual(DisplayGeometrySource.MonitorInfo, result.Source);
        Assert.AreEqual(Gdi, result.Geometry);
    }

    [TestMethod]
    public void Invalid_source_mode_index_or_ambiguous_mapping_omits_display_config_and_falls_back_safely()
    {
        var invalidIndexResult = DisplayGeometryResolver.Resolve(Gdi, null, DevMode);
        var ambiguousMappingResult = DisplayGeometryResolver.Resolve(Gdi, null, DevMode);

        Assert.AreEqual(DevMode, invalidIndexResult.Geometry);
        Assert.AreEqual(DevMode, ambiguousMappingResult.Geometry);
    }

    [TestMethod]
    public void Virtual_mode_aware_source_index_uses_documented_upper_sixteen_bits()
    {
        const uint packed = (37u << 16) | 12u;

        Assert.AreEqual(12u, DisplayConfigSourceModeIndex.Decode(12u, supportsVirtualMode: false));
        Assert.AreEqual(37u, DisplayConfigSourceModeIndex.Decode(packed, supportsVirtualMode: true));
    }

    [TestMethod]
    public void Invalid_or_out_of_range_source_mode_index_is_rejected()
    {
        Assert.IsFalse(DisplayConfigSourceModeIndex.TryDecode(
            DisplayConfigSourceModeIndex.Invalid, supportsVirtualMode: false, modeCount: 4, out _));
        Assert.IsFalse(DisplayConfigSourceModeIndex.TryDecode(
            9, supportsVirtualMode: false, modeCount: 4, out _));
        Assert.IsTrue(DisplayConfigSourceModeIndex.TryDecode(
            (3u << 16) | 1u, supportsVirtualMode: true, modeCount: 4, out var index));
        Assert.AreEqual(3u, index);
    }

    [TestMethod]
    public void Geometry_with_overflowing_edge_is_invalid_and_cannot_mix_with_fallback()
    {
        var invalid = new DisplayGeometry(int.MaxValue, 10, 100, 100);
        var result = DisplayGeometryResolver.Resolve(Gdi, invalid, DevMode);

        Assert.IsFalse(invalid.IsValid);
        Assert.AreEqual(DevMode, result.Geometry);
        Assert.AreEqual(-7040, result.Geometry.X);
        Assert.AreEqual(5120, result.Geometry.Width);
    }

    [DataTestMethod]
    [DataRow(DisplayConfigRotation.Identity, 1920, 1080)]
    [DataRow(DisplayConfigRotation.Rotate180, 1920, 1080)]
    [DataRow(DisplayConfigRotation.Rotate90, 1080, 1920)]
    [DataRow(DisplayConfigRotation.Rotate270, 1080, 1920)]
    public void Source_mode_is_converted_to_rotation_aware_desktop_footprint(
        DisplayConfigRotation rotation,
        int expectedWidth,
        int expectedHeight)
    {
        var result = DisplayConfigSourceModeFootprint.FromSourceMode(
            new(-1234, 567, 1920, 1080),
            rotation);

        Assert.AreEqual(-1234, result.X);
        Assert.AreEqual(567, result.Y);
        Assert.AreEqual(expectedWidth, result.Width);
        Assert.AreEqual(expectedHeight, result.Height);
    }

    [TestMethod]
    public void Non_sixteen_by_nine_source_mode_swaps_only_dimensions_for_rotate90()
    {
        var result = DisplayConfigSourceModeFootprint.FromSourceMode(
            new(10, -20, 3440, 1440),
            DisplayConfigRotation.Rotate90);

        Assert.AreEqual(new DisplayGeometry(10, -20, 1440, 3440), result);
    }

    [DataTestMethod]
    [DataRow(-4080, -841, -3000, 1079)]
    [DataRow(-1080, -847, 0, 1073)]
    public void Real_portrait_vdd_source_modes_match_their_desktop_footprints(
        int x,
        int y,
        int expectedRight,
        int expectedBottom)
    {
        var result = DisplayConfigSourceModeFootprint.FromSourceMode(
            new(x, y, 1920, 1080),
            DisplayConfigRotation.Rotate90);

        Assert.AreEqual(new DisplayGeometry(x, y, 1080, 1920), result);
        Assert.AreEqual(expectedRight, result.Right);
        Assert.AreEqual(expectedBottom, result.Bottom);
    }
}
