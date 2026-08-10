using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Wallflow.Core;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Wallflow;

public sealed partial class MainWindow : Window
{
    private readonly IMonitorService _monitors = new WindowsMonitorService();
    private readonly IWallpaperService _wallpaper = new DesktopWallpaperService();
    private readonly string _settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pane");
    private readonly ProfileStore _store;
    private readonly AppWindow _appWindow;
    private readonly TrayIconService _trayIcon;
    private readonly Dictionary<string, SlideshowSession> _sessions = [];
    private List<MonitorInfo> _displayList = []; private List<MonitorWallpaperProfile> _profiles = []; private MonitorInfo? _selected;
    private bool _initialized;
    private TextBlock _setupNameText = null!;
    private TextBox _setupNameEditor = null!;
    private StackPanel _setupNameDisplay = null!;
    private readonly TextBlock _setupNameMeasure = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private bool _setupNameEditClosing;
    private bool _exitRequested;

    public MainWindow()
    {
        _store = new ProfileStore(Path.Combine(_settingsFolder, "profiles.json"));
        InitializeComponent(); ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        _appWindow = GetAppWindow(); _appWindow.Resize(new SizeInt32(1100, 800));
        var windowIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Pane.ico");
        if (File.Exists(windowIconPath)) _appWindow.SetIcon(windowIconPath);
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent; _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _trayIcon = new TrayIconService(ShowFromTray, ExitFromTray);
        _appWindow.Closing += (_, args) =>
        {
            if (_exitRequested) return;
            args.Cancel = true; _appWindow.Hide(); _trayIcon.ShowBackgroundNotice();
        };
        RootGrid.Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            InitializeSetupHeader();
            await InitializeAsync();
        };
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), true);
        Closed += async (_, _) =>
        {
            _trayIcon.Dispose();
            foreach (var session in _sessions.Values) session.Stop();
            foreach (var session in _sessions.Values) await session.DisposeAsync();
            await _store.SaveAsync(_profiles);
        };
    }
    private void ShowFromTray() { _appWindow.Show(); Activate(); }
    private void ExitFromTray() { _exitRequested = true; Close(); }
    private void InitializeSetupHeader()
    {
        var headingStack = (StackPanel)SetupSummary.Parent;
        var headingGrid = (Grid)headingStack.Parent;
        var actionStack = (StackPanel)headingGrid.Children[1];
        if (headingGrid.ColumnDefinitions.Count == 0)
        {
            headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        Grid.SetColumn(headingStack, 0); Grid.SetColumn(actionStack, 1);
        headingStack.Children.RemoveAt(0);
        _setupNameText = new TextBlock { Text = "Your setup", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _setupNameText.PointerPressed += (_, _) => BeginSetupNameEdit();
        var pencilButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE70F", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 },
            Style = (Style)Application.Current.Resources["JellyIconButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(pencilButton, "Rename setup"); pencilButton.Click += (_, _) => BeginSetupNameEdit();
        _setupNameDisplay = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _setupNameDisplay.Children.Add(_setupNameText); _setupNameDisplay.Children.Add(pencilButton);
        _setupNameEditor = new TextBox { Visibility = Visibility.Collapsed, Opacity = 0, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 110, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)Application.Current.Resources["JellyTextBoxStyle"] };
        _setupNameEditor.KeyDown += SetupNameEditor_KeyDown;
        _setupNameEditor.TextChanged += (_, _) => ResizeSetupNameEditor();
        _setupNameEditor.LostFocus += (_, _) => _ = CloseSetupNameEditorAsync(save: true);
        var host = new Grid(); host.Children.Add(_setupNameDisplay); host.Children.Add(_setupNameEditor); headingStack.Children.Insert(0, host);
    }
    private void BeginSetupNameEdit()
    {
        if (_setupNameEditor.Visibility == Visibility.Visible) return;
        _setupNameEditor.Text = _setupNameText.Text; ResizeSetupNameEditor();
        Fade(_setupNameDisplay, 1, 0, 100, () =>
        {
            _setupNameDisplay.Visibility = Visibility.Collapsed; _setupNameEditor.Visibility = Visibility.Visible;
            Fade(_setupNameEditor, 0, 1, 130); _setupNameEditor.Focus(FocusState.Programmatic); _setupNameEditor.SelectAll();
        });
    }
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_setupNameEditor.Visibility != Visibility.Visible || e.OriginalSource is not DependencyObject source || IsInside(source, _setupNameEditor)) return;
        _ = CloseSetupNameEditorAsync(save: true);
    }
    private static bool IsInside(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private void ResizeSetupNameEditor()
    {
        _setupNameMeasure.Text = string.IsNullOrEmpty(_setupNameEditor.Text) ? "Setup" : _setupNameEditor.Text;
        _setupNameMeasure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _setupNameEditor.Width = Math.Clamp(_setupNameMeasure.DesiredSize.Width + 42, 110, 440);
    }
    private void EndSetupNameEdit(Action? completed = null)
    {
        Fade(_setupNameEditor, 1, 0, 100, () =>
        {
            _setupNameEditor.Visibility = Visibility.Collapsed; _setupNameDisplay.Visibility = Visibility.Visible;
            Fade(_setupNameDisplay, 0, 1, 130, completed);
        });
    }
    private static void Fade(UIElement element, double from, double to, int milliseconds, Action? completed = null)
    {
        element.Opacity = from; var animation = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)), EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, element); Storyboard.SetTargetProperty(animation, "Opacity"); var storyboard = new Storyboard(); storyboard.Children.Add(animation);
        if (completed is not null) storyboard.Completed += (_, _) => completed(); storyboard.Begin();
    }
    private async void SetupNameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { _ = CloseSetupNameEditorAsync(save: false); e.Handled = true; return; }
        if (e.Key != VirtualKey.Enter) return;
        await CloseSetupNameEditorAsync(save: true); e.Handled = true;
    }
    private async Task CloseSetupNameEditorAsync(bool save)
    {
        if (_setupNameEditClosing || _setupNameEditor.Visibility != Visibility.Visible) return;
        _setupNameEditClosing = true;
        if (save)
        {
            var name = _setupNameEditor.Text.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                _setupNameText.Text = name; Directory.CreateDirectory(_settingsFolder); await File.WriteAllTextAsync(Path.Combine(_settingsFolder, "setup-name.txt"), name);
            }
        }
        EndSetupNameEdit(() => _setupNameEditClosing = false);
    }
    private AppWindow GetAppWindow() => AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
    private async Task InitializeAsync()
    {
        MigrateLegacyProfiles();
        var setupNamePath = Path.Combine(_settingsFolder, "setup-name.txt");
        if (File.Exists(setupNamePath))
        {
            var savedName = (await File.ReadAllTextAsync(setupNamePath)).Trim();
            if (!string.IsNullOrWhiteSpace(savedName)) _setupNameText.Text = savedName;
        }
        try { _profiles = await _store.LoadAsync(); } catch { _profiles = []; }
        await RefreshAsync();
        foreach (var display in _displayList)
        {
            var profile = Profile(display);
            if (profile.Mode == WallpaperMode.Slideshow && profile.Enabled && ImageCatalog.Scan(profile.SlideshowFolderPath ?? "").Count > 0)
                await StartSlideshowAsync(display, profile);
        }
    }
    private void MigrateLegacyProfiles()
    {
        Directory.CreateDirectory(_settingsFolder);
        var paneProfiles = Path.Combine(_settingsFolder, "profiles.json");
        var legacyProfiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallflow", "profiles.json");
        if (!File.Exists(paneProfiles) && File.Exists(legacyProfiles)) File.Copy(legacyProfiles, paneProfiles);
    }
    private async Task RefreshAsync()
    {
        _displayList = (await _monitors.GetMonitorsAsync()).ToList();
        foreach (var display in _displayList)
            if (_profiles.All(p => p.MonitorId != display.Id)) _profiles.Add(new() { MonitorId = display.Id, MonitorDevicePath = display.DeviceName });
        SetupSummary.Text = $"{_displayList.Count} {(_displayList.Count == 1 ? "display" : "displays")} • {_profiles.Count(p => p.Mode == WallpaperMode.Slideshow && p.Enabled)} slideshows configured";
        RenderMonitors(); if (_selected is null && _displayList.Count > 0) SelectMonitor(_displayList[0]);
    }
    private void RenderMonitors()
    {
        MonitorCanvas.Children.Clear(); if (_displayList.Count == 0) return;
        var minX = _displayList.Min(x => x.X); var minY = _displayList.Min(x => x.Y); var maxX = _displayList.Max(x => x.X + x.Width); var maxY = _displayList.Max(x => x.Y + x.Height);
        var availableW = Math.Max(500, MonitorCanvas.ActualWidth - 60); var availableH = Math.Max(80, MonitorCanvas.ActualHeight - 72);
        var scale = Math.Min(availableW / (maxX - minX), availableH / (maxY - minY)) * .78;
        var xPositions = _displayList.Select(display => display.X).Distinct().Order().ToArray();
        const double monitorGap = 30;
        var layoutW = (maxX - minX) * scale + Math.Max(0, xPositions.Length - 1) * monitorGap; var layoutH = (maxY - minY) * scale;
        foreach (var display in _displayList)
        {
            // Scale each screen with one factor so its detected aspect ratio is never distorted.
            var w = display.Width * scale; var h = display.Height * scale;
            var minimumLongEdge = display.IsPortrait ? 100d : 125d;
            var longEdge = display.IsPortrait ? h : w;
            var sizeFactor = longEdge < minimumLongEdge ? minimumLongEdge / longEdge : 1d;
            var maximumHeight = Math.Max(100, MonitorCanvas.ActualHeight - 70);
            var maximumWidth = display.IsPortrait ? 120d : 250d;
            sizeFactor = Math.Min(sizeFactor, Math.Min(maximumWidth / w, maximumHeight / h));
            w = Math.Max(48, w * sizeFactor); h = Math.Max(48, h * sizeFactor);
            var horizontalRank = Array.IndexOf(xPositions, display.X);
            var card = CreateMonitor(display, w, h); Canvas.SetLeft(card, (MonitorCanvas.ActualWidth - layoutW) / 2 + (display.X - minX) * scale + horizontalRank * monitorGap); Canvas.SetTop(card, 12 + (display.Y - minY) * scale); MonitorCanvas.Children.Add(card);
        }
    }
    private FrameworkElement CreateMonitor(MonitorInfo display, double width, double height)
    {
        var profile = Profile(display); var selected = _selected?.Id == display.Id; var container = new Grid { Width = width + 20, Height = height + 62, Tag = display };
        var glow = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(20), Background = selected ? new SolidColorBrush(ColorHelper.FromArgb(48, 110, 135, 255)) : new SolidColorBrush(Colors.Transparent) };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var frame = new Border { Height = height, Margin = new Thickness(10, 4, 10, 0), Padding = new Thickness(5), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 35, 38, 47)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(selected ? ColorHelper.FromArgb(255, 124, 140, 255) : ColorHelper.FromArgb(28, 255, 255, 255)) };
        var screen = new Grid { Background = new LinearGradientBrush { StartPoint = new(.1, 0), EndPoint = new(.9, 1), GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(255, 33, 45, 74) }, new GradientStop { Color = ColorHelper.FromArgb(255, 87, 62, 105), Offset = 1 } } } };
        var path = profile.LastWallpaperPath ?? profile.StaticImagePath;
        if (File.Exists(path))
        {
            var previewStretch = profile.FitMode switch { WallpaperFit.Fill => Stretch.UniformToFill, WallpaperFit.Fit => Stretch.Uniform, WallpaperFit.Stretch => Stretch.Fill, WallpaperFit.Center => Stretch.None, _ => Stretch.UniformToFill };
            screen.Children.Add(new Image { Source = new BitmapImage(new Uri(path)), Stretch = previewStretch, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        screen.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Top, Height = height * .22, Background = new LinearGradientBrush { StartPoint = new(.5, 0), EndPoint = new(.5, 1), GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(18, 255, 255, 255) }, new GradientStop { Color = Colors.Transparent, Offset = 1 } } } }); frame.Child = screen;
        stack.Children.Add(frame); stack.Children.Add(new Border { Width = 12, Height = 13, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 51, 60)), HorizontalAlignment = HorizontalAlignment.Center }); stack.Children.Add(new Border { Width = Math.Min(68, width * .4), Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 55, 58, 68)), HorizontalAlignment = HorizontalAlignment.Center });
        var label = new TextBlock { Text = display.FriendlyName + (display.IsPrimary ? "  •  PRIMARY" : ""), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) }; stack.Children.Add(label);
        container.Children.Add(glow); container.Children.Add(stack); container.PointerPressed += (_, _) => SelectMonitor(display); return container;
    }
    private MonitorWallpaperProfile Profile(MonitorInfo info) => _profiles.First(p => p.MonitorId == info.Id);
    private void SelectMonitor(MonitorInfo monitor)
    {
        _selected = monitor; var profile = Profile(monitor); Editor.IsHitTestVisible = true; Editor.Opacity = 1; SelectedName.Text = monitor.FriendlyName;
        var displayDetails = new List<string> { monitor.Resolution };
        if (monitor.IsPrimary) displayDetails.Add("Primary display");
        displayDetails.Add(monitor.RefreshRate > 1 ? $"{monitor.RefreshRate} Hz" : "Refresh rate unavailable");
        SelectedDetails.Text = string.Join("  •  ", displayDetails);
        ModeToggle.IsOn = profile.Mode == WallpaperMode.Slideshow; ImagePathText.Text = profile.StaticImagePath ?? "No image selected"; FolderPathText.Text = profile.SlideshowFolderPath ?? "No folder selected"; ShuffleToggle.IsOn = profile.ShuffleEnabled; LoopToggle.IsOn = profile.LoopEnabled; FitBox.SelectedIndex = (int)profile.FitMode;
        SetPreview(profile.LastWallpaperPath ?? profile.StaticImagePath); ValidationText.Text = ""; RenderMonitors();
    }
    private void SetPreview(string? path) { var exists = File.Exists(path); EmptyPreview.Visibility = exists ? Visibility.Collapsed : Visibility.Visible; WallpaperPreview.Source = exists ? new BitmapImage(new Uri(path!)) : null; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderMonitors();
    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "Display identification overlays are planned; refresh and topology detection are active now.";
    }
    private void ModeToggle_Toggled(object sender, RoutedEventArgs e) { StaticPanel.Visibility = ModeToggle.IsOn ? Visibility.Collapsed : Visibility.Visible; SlideshowPanel.Visibility = ModeToggle.IsOn ? Visibility.Visible : Visibility.Collapsed; }
    private void FitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is null || FitBox.SelectedIndex < 0) return;
        Profile(_selected).FitMode = (WallpaperFit)FitBox.SelectedIndex;
        RenderMonitors();
    }
    private async void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; var picker = new FileOpenPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync(); if (file is null) return; Profile(_selected).StaticImagePath = file.Path; ImagePathText.Text = file.Path; SetPreview(file.Path); ValidationText.Text = "";
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; var picker = new FolderPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); picker.FileTypeFilter.Add("*"); var folder = await picker.PickSingleFolderAsync(); if (folder is null) return; Profile(_selected).SlideshowFolderPath = folder.Path; FolderPathText.Text = folder.Path; ValidationText.Text = $"{ImageCatalog.Scan(folder.Path).Count} supported images";
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; var profile = Profile(_selected); profile.Mode = ModeToggle.IsOn ? WallpaperMode.Slideshow : WallpaperMode.Static; profile.ShuffleEnabled = ShuffleToggle.IsOn; profile.LoopEnabled = LoopToggle.IsOn; profile.FitMode = (WallpaperFit)Math.Max(0, FitBox.SelectedIndex);
        try
        {
            if (profile.Mode == WallpaperMode.Static) { if (!File.Exists(profile.StaticImagePath) || !ImageCatalog.IsSupported(profile.StaticImagePath!)) { ValidationText.Text = "Choose a supported wallpaper image first."; return; } await _wallpaper.SetWallpaperAsync(_selected.Id, profile.StaticImagePath!, profile.FitMode); profile.LastWallpaperPath = profile.StaticImagePath; }
            else { var files = ImageCatalog.Scan(profile.SlideshowFolderPath ?? ""); if (files.Count == 0) { ValidationText.Text = "This folder doesn't contain any supported images."; return; } if (IntervalBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var minutes)) profile.SlideshowInterval = TimeSpan.FromMinutes(minutes); await StartSlideshowAsync(_selected, profile); }
            await _store.SaveAsync(_profiles); ValidationText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 116, 221, 164)); ValidationText.Text = profile.Mode == WallpaperMode.Static ? "Wallpaper applied" : "Slideshow started"; SetupSummary.Text = $"{_displayList.Count} displays • {_profiles.Count(p => p.Mode == WallpaperMode.Slideshow && p.Enabled)} slideshows configured"; RenderMonitors();
        }
        catch (Exception ex) { ValidationText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 123, 134)); ValidationText.Text = ex.Message; }
    }
    private async Task StartSlideshowAsync(MonitorInfo monitor, MonitorWallpaperProfile profile)
    {
        if (_sessions.Remove(monitor.Id, out var old)) await old.DisposeAsync();
        var session = new SlideshowSession(monitor, profile, new WallpaperTransitionService(_wallpaper));
        session.WallpaperChanged += (_, path) => DispatcherQueue.TryEnqueue(() => { if (_selected?.Id == monitor.Id) SetPreview(path); RenderMonitors(); });
        _sessions[monitor.Id] = session; session.Start();
    }
}
