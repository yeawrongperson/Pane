using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class WallpaperPreviewLayoutTests
{
    private const double Tolerance = 0.0001;

    [TestMethod]
    public void Landscape_monitor_produces_centered_sixteen_by_nine_viewport()
    {
        var viewport = WallpaperPreviewLayout.Calculate(400, 200, 1920, 1080);

        AssertViewport(viewport, 22.2222, 0, 355.5556, 200);
        Assert.AreEqual(16d / 9, viewport.Width / viewport.Height, Tolerance);
    }

    [TestMethod]
    public void Equivalent_monitor_resolutions_produce_the_same_viewport()
    {
        var fullHd = WallpaperPreviewLayout.Calculate(400, 200, 1920, 1080);
        var quadHd = WallpaperPreviewLayout.Calculate(400, 200, 2560, 1440);

        Assert.AreEqual(fullHd, quadHd);
    }

    [TestMethod]
    public void Portrait_monitor_produces_centered_portrait_viewport()
    {
        var viewport = WallpaperPreviewLayout.Calculate(400, 200, 1080, 1920);

        AssertViewport(viewport, 143.75, 0, 112.5, 200);
    }

    [TestMethod]
    public void Ultrawide_monitor_produces_centered_ultrawide_viewport()
    {
        var viewport = WallpaperPreviewLayout.Calculate(400, 200, 3440, 1440);

        AssertViewport(viewport, 0, 16.2791, 400, 167.4419);
        Assert.AreEqual(3440d / 1440, viewport.Width / viewport.Height, Tolerance);
    }

    [DataTestMethod]
    [DataRow(400, 200, 1920, 1080)]
    [DataRow(400, 200, 1080, 1920)]
    [DataRow(400, 200, 3440, 1440)]
    [DataRow(175, 310, 5120, 1440)]
    public void Viewport_always_fits_entirely_inside_stage(
        double stageWidth,
        double stageHeight,
        double monitorWidth,
        double monitorHeight)
    {
        var viewport = WallpaperPreviewLayout.Calculate(stageWidth, stageHeight, monitorWidth, monitorHeight);

        Assert.IsTrue(viewport.X >= 0);
        Assert.IsTrue(viewport.Y >= 0);
        Assert.IsTrue(viewport.X + viewport.Width <= stageWidth + Tolerance);
        Assert.IsTrue(viewport.Y + viewport.Height <= stageHeight + Tolerance);
    }

    [TestMethod]
    public void Source_image_dimensions_have_no_input_to_viewport_geometry()
    {
        var beforeSourceChange = WallpaperPreviewLayout.Calculate(400, 200, 1920, 1080);
        var afterSourceChange = WallpaperPreviewLayout.Calculate(400, 200, 1920, 1080);

        Assert.AreEqual(beforeSourceChange, afterSourceChange);
    }

    [DataTestMethod]
    [DataRow(0, 1080)]
    [DataRow(1920, 0)]
    [DataRow(double.NaN, 1080)]
    [DataRow(double.PositiveInfinity, 1080)]
    public void Invalid_monitor_dimensions_fall_back_to_stage_without_non_finite_geometry(
        double monitorWidth,
        double monitorHeight)
    {
        var viewport = WallpaperPreviewLayout.Calculate(400, 200, monitorWidth, monitorHeight);

        AssertViewport(viewport, 0, 0, 400, 200);
    }

    [TestMethod]
    public void Invalid_stage_dimensions_return_safe_empty_viewport()
    {
        Assert.AreEqual(default, WallpaperPreviewLayout.Calculate(0, 200, 1920, 1080));
        Assert.AreEqual(default, WallpaperPreviewLayout.Calculate(double.NaN, 200, 1920, 1080));
    }

    private static void AssertViewport(
        WallpaperPreviewViewport actual,
        double x,
        double y,
        double width,
        double height)
    {
        Assert.AreEqual(x, actual.X, Tolerance);
        Assert.AreEqual(y, actual.Y, Tolerance);
        Assert.AreEqual(width, actual.Width, Tolerance);
        Assert.AreEqual(height, actual.Height, Tolerance);
        Assert.IsTrue(double.IsFinite(actual.X));
        Assert.IsTrue(double.IsFinite(actual.Y));
        Assert.IsTrue(double.IsFinite(actual.Width));
        Assert.IsTrue(double.IsFinite(actual.Height));
    }
}
