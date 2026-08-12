using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Wallflow.Core;
using Windows.Foundation;
using Windows.UI;

namespace Wallflow;

internal enum MonitorShellRenderMode { Full, Compact }

internal sealed record MonitorShellRequest(
    MonitorVisualDescriptor Descriptor,
    double Width,
    double Height,
    string? WallpaperPath,
    WallpaperFit FitMode,
    bool IsSelected,
    bool IsConnected,
    MonitorShellRenderMode Mode,
    string? AutomationName = null);

/// <summary>Builds all monitor chrome procedurally; no raster shell assets are used.</summary>
internal static class MonitorShellRenderer
{
    private static readonly Color Bezel = ColorHelper.FromArgb(255, 25, 27, 33);
    private static readonly Color BezelEdge = ColorHelper.FromArgb(255, 58, 61, 70);
    private static readonly Color Stand = ColorHelper.FromArgb(255, 47, 50, 58);

    public static FrameworkElement Create(MonitorShellRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var width = Math.Max(4, request.Width);
        var height = Math.Max(4, request.Height);
        var full = request.Mode == MonitorShellRenderMode.Full;
        var totalHeight = height + (full ? FullChromeHeight(height) : 0);
        var root = new Grid { Width = width, Height = totalHeight, Opacity = request.IsConnected ? 1 : .48 };
        root.IsHitTestVisible = request.Mode == MonitorShellRenderMode.Full;
        if (!string.IsNullOrWhiteSpace(request.AutomationName)) AutomationProperties.SetName(root, request.AutomationName);

        var panelHeight = height;
        switch (request.Descriptor.ResolvedShellStyle)
        {
            case DisplayShellStyle.UltrawideCurved:
                if (full) AddStand(root, width, panelHeight, totalHeight, wide: true);
                root.Children.Add(CreateCurvedPanel(request, width, panelHeight));
                break;
            case DisplayShellStyle.Laptop:
                root.Children.Add(CreateFlatPanel(request, width, full ? height : height * .88, 6, 3));
                AddLaptopBase(root, width, full ? height : height * .88, totalHeight);
                break;
            case DisplayShellStyle.LargeDisplay:
                if (full) AddFeet(root, width, height, totalHeight);
                root.Children.Add(CreateFlatPanel(request, width, height, 5, 2));
                break;
            case DisplayShellStyle.UltrawideFlat:
                if (full) AddStand(root, width, panelHeight, totalHeight, wide: true);
                root.Children.Add(CreateFlatPanel(request, width, panelHeight, 7, 3));
                break;
            default:
                if (full) AddStand(root, width, panelHeight, totalHeight, wide: false);
                root.Children.Add(CreateFlatPanel(request, width, panelHeight, 7, 3));
                break;
        }
        return root;
    }

    public static double FullChromeHeight(double screenHeight) => Math.Clamp(screenHeight * .20, 5, 18);

    private static FrameworkElement CreateFlatPanel(MonitorShellRequest request, double width, double height, double outerRadius, double bezel)
    {
        bezel = Math.Min(bezel, Math.Min(width, height) * .12);
        var selected = request.IsSelected;
        var frame = new Border
        {
            Width = width, Height = height, Padding = new Thickness(bezel),
            CornerRadius = new CornerRadius(Math.Min(outerRadius, height / 4)),
            Background = new SolidColorBrush(Bezel),
            BorderBrush = new SolidColorBrush(selected ? ColorHelper.FromArgb(210, 124, 140, 255) : BezelEdge),
            BorderThickness = new Thickness(selected ? 1.6 : 1)
        };
        var screenWidth = Math.Max(1, width - bezel * 2);
        var screenHeight = Math.Max(1, height - bezel * 2);
        frame.Child = CreateScreen(request, screenWidth, screenHeight,
            new RectangleGeometry { Rect = new Rect(0, 0, screenWidth, screenHeight) });
        return frame;
    }

    private static FrameworkElement CreateCurvedPanel(MonitorShellRequest request, double width, double height)
    {
        var depth = Math.Min(width * .025, Math.Max(1.5, height * .07));
        var outerGeometry = CurvedGeometry(width, height, depth);
        var border = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = outerGeometry,
            Fill = new SolidColorBrush(Bezel),
            Stroke = new SolidColorBrush(request.IsSelected ? ColorHelper.FromArgb(220, 124, 140, 255) : BezelEdge),
            StrokeThickness = request.IsSelected ? 1.6 : 1
        };
        var inset = Math.Min(3, Math.Min(width, height) * .08);
        var screenWidth = Math.Max(1, width - inset * 2);
        var screenHeight = Math.Max(1, height - inset * 2);
        var screenGeometry = CurvedGeometry(screenWidth, screenHeight, Math.Max(1, depth * .72));
        var screen = CreateCurvedScreen(request, screenWidth, screenHeight, screenGeometry);
        screen.Margin = new Thickness(inset);
        var grid = new Grid { Width = width, Height = height };
        grid.Children.Add(border);
        grid.Children.Add(screen);
        return grid;
    }

    private static Grid CreateScreen(MonitorShellRequest request, double width, double height, RectangleGeometry clip)
    {
        var screen = new Grid
        {
            Width = width, Height = height, Clip = clip,
            Background = new LinearGradientBrush
            {
                StartPoint = new(.08, 0), EndPoint = new(.92, 1),
                GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(255, 29, 42, 72) }, new GradientStop { Color = ColorHelper.FromArgb(255, 89, 58, 102), Offset = 1 } }
            }
        };
        if (!string.IsNullOrWhiteSpace(request.WallpaperPath) && File.Exists(request.WallpaperPath))
        {
            try
            {
                screen.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri(request.WallpaperPath)),
                    Stretch = PreviewStretch(request.FitMode),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            catch { }
        }
        if (request.Mode == MonitorShellRenderMode.Full)
            screen.Children.Add(new Border { Height = height * .18, VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush { StartPoint = new(.5, 0), EndPoint = new(.5, 1), GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(18, 255, 255, 255) }, new GradientStop { Color = Colors.Transparent, Offset = 1 } } } });
        return screen;
    }

    private static FrameworkElement CreateCurvedScreen(MonitorShellRequest request, double width, double height, PathGeometry geometry)
    {
        Brush fill = new LinearGradientBrush
        {
            StartPoint = new(.08, 0), EndPoint = new(.92, 1),
            GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(255, 29, 42, 72) }, new GradientStop { Color = ColorHelper.FromArgb(255, 89, 58, 102), Offset = 1 } }
        };
        if (!string.IsNullOrWhiteSpace(request.WallpaperPath) && File.Exists(request.WallpaperPath))
        {
            try
            {
                fill = new ImageBrush { ImageSource = new BitmapImage(new Uri(request.WallpaperPath)),
                    Stretch = PreviewStretch(request.FitMode), AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            }
            catch { }
        }
        return new Microsoft.UI.Xaml.Shapes.Path { Width = width, Height = height, Data = geometry, Fill = fill };
    }

    private static PathGeometry CurvedGeometry(double width, double height, double depth)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(depth, 0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new BezierSegment { Point1 = new(width * .30, depth * .35), Point2 = new(width * .70, depth * .35), Point3 = new(width - depth, 0) });
        figure.Segments.Add(new BezierSegment { Point1 = new(width, height * .28), Point2 = new(width, height * .72), Point3 = new(width - depth, height) });
        figure.Segments.Add(new BezierSegment { Point1 = new(width * .70, height - depth * .35), Point2 = new(width * .30, height - depth * .35), Point3 = new(depth, height) });
        figure.Segments.Add(new BezierSegment { Point1 = new(0, height * .72), Point2 = new(0, height * .28), Point3 = new(depth, 0) });
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static void AddStand(Grid root, double width, double panelHeight, double totalHeight, bool wide)
    {
        var neckHeight = Math.Max(2, (totalHeight - panelHeight) * .56);
        root.Children.Add(new Border { Width = Math.Max(3, Math.Min(12, width * .06)), Height = neckHeight,
            Margin = new Thickness(0, panelHeight, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Stand) });
        root.Children.Add(new Border { Width = Math.Max(10, Math.Min(wide ? 84 : 64, width * (wide ? .42 : .34))), Height = Math.Max(2, totalHeight * .035),
            Margin = new Thickness(0, panelHeight + neckHeight, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(BezelEdge) });
    }

    private static void AddFeet(Grid root, double width, double panelHeight, double totalHeight)
    {
        foreach (var alignment in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
            root.Children.Add(new Border { Width = Math.Max(5, width * .12), Height = Math.Max(2, totalHeight - panelHeight), Margin = new Thickness(width * .12, panelHeight, width * .12, 0),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = alignment, Background = new SolidColorBrush(Stand), CornerRadius = new CornerRadius(2) });
    }

    private static void AddLaptopBase(Grid root, double width, double panelHeight, double totalHeight)
    {
        var deck = new Microsoft.UI.Xaml.Shapes.Path { Fill = new SolidColorBrush(BezelEdge), Data = new PathGeometry() };
        var figure = new PathFigure { StartPoint = new Point(width * .06, panelHeight), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(width * .94, panelHeight) });
        figure.Segments.Add(new LineSegment { Point = new Point(width, Math.Min(totalHeight, panelHeight + Math.Max(2, totalHeight * .12))) });
        figure.Segments.Add(new LineSegment { Point = new Point(0, Math.Min(totalHeight, panelHeight + Math.Max(2, totalHeight * .12))) });
        ((PathGeometry)deck.Data).Figures.Add(figure);
        root.Children.Add(deck);
    }

    private static Stretch PreviewStretch(WallpaperFit fitMode) => fitMode switch
    {
        WallpaperFit.Fill => Stretch.UniformToFill,
        WallpaperFit.Fit => Stretch.Uniform,
        WallpaperFit.Stretch => Stretch.Fill,
        WallpaperFit.Center => Stretch.None,
        _ => Stretch.UniformToFill
    };
}
