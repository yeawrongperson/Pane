using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using System.Runtime.CompilerServices;
using Wallflow.Core;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Wallflow;

public sealed partial class MainWindow : Window
{
    private readonly PaneStartupOptions _startupOptions;
    private readonly IMonitorService? _monitors;
    private readonly IWallpaperService? _wallpaper;
    private readonly string? _settingsFolder;
    private readonly SetupStateStore? _setupStore;
    private readonly AppWindow _appWindow;
    private readonly TrayIconService? _trayIcon;
    private System.Drawing.Icon? _windowIcon;
    private readonly Dictionary<string, SlideshowSession> _sessions = [];
    private readonly LatestScanCoordinator<string> _folderScans = new();
    private readonly LatestSetupSwitchCoordinator _setupSwitches = new();
    private readonly SetupUndoTracker _undoTracker = new();
    private List<MonitorInfo> _displayList = []; private List<MonitorWallpaperProfile> _profiles = []; private MonitorInfo? _selected;
    private SetupManager? _setupManager;
    private DispatcherQueueTimer? _statusTimer;
    private Storyboard? _statusStoryboard;
    private readonly ConditionalWeakTable<Button, ButtonMotionState> _buttonMotionStates = new();
    private bool _canSaveSetupState;
    private bool _updatingEditor;
    private bool _initialized;
    private bool _exitRequested;
    private bool _draggingSetupPanel;
    private uint _setupPanelPointerId;
    private Windows.Foundation.Point _setupPanelDragStart;
    private FloatingPanelPosition _setupPanelDragOrigin;
    private bool _editingMonitorName;
    private MonitorInfo? _monitorNameEditTarget;
    private string? _editingSetupNameId;
    private TextBox? _setupNameEditor;
    private TextBlock? _setupNameDisplay;
    private Button? _setupNameFocusTarget;
    private SpriteVisual? _setupPanelShadowVisual;
    private CompositionRoundedRectangleGeometry? _setupPanelShadowGeometry;

    public MainWindow(PaneStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        if (startupOptions.UsesPersistentProfileState)
        {
            _monitors = new WindowsMonitorService();
            _settingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pane");
            _setupStore = new SetupStateStore(
                Path.Combine(_settingsFolder, "setups.json"),
                Path.Combine(_settingsFolder, "profiles.json"),
                Path.Combine(_settingsFolder, "setup-name.txt"));
        }
        if (startupOptions.AllowsWallpaperChanges) _wallpaper = new DesktopWallpaperService();
        InitializeComponent(); ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        _appWindow = GetAppWindow(); _appWindow.Resize(new SizeInt32(1100, 800));
        SetEmbeddedWindowIcon();
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent; _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (startupOptions.CreatesTrayIcon) _trayIcon = new TrayIconService(ShowAndActivate, ExitFromTray);
        _appWindow.Closing += (_, args) =>
        {
            if (_startupOptions.IsSmokeTest || _exitRequested) return;
            args.Cancel = true; _appWindow.Hide(); _trayIcon!.ShowBackgroundNotice();
        };
        RootGrid.Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            if (_startupOptions.IsSmokeTest)
            {
                RootGrid.IsHitTestVisible = false;
                if (!DispatcherQueue.TryEnqueue(CompleteSmokeTest))
                    throw new InvalidOperationException("Pane smoke test could not schedule clean shutdown.");
                return;
            }
            AttachButtonMotionTree(RootGrid);
            EnableButtonMotion(ApplyButton);
            await InitializeAsync();
        };
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        RootGrid.KeyDown += RootGrid_KeyDown;
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), true);
        SetupPanel.SizeChanged += SetupPanel_SizeChanged;
        Closed += async (_, _) =>
        {
            await _setupSwitches.DisposeAsync();
            _folderScans.Dispose();
            _trayIcon?.Dispose();
            _windowIcon?.Dispose();
            _windowIcon = null;
            if (_startupOptions.IsSmokeTest)
            {
                Application.Current.Exit();
                return;
            }
            CommitEditorToProfile();
            await StopAllSessionsAsync();
            await SaveSetupStateAsync(showFailure: false);
        };
    }
    private string SettingsFolder => _settingsFolder ?? throw new InvalidOperationException("Persistent Pane state is disabled.");
    private SetupStateStore SetupStore => _setupStore ?? throw new InvalidOperationException("Persistent Pane state is disabled.");
    private SetupManager Setups => _setupManager ?? throw new InvalidOperationException("Pane setups have not been initialized.");
    private IMonitorService Monitors => _monitors ?? throw new InvalidOperationException("Monitor initialization is disabled.");
    private IWallpaperService Wallpaper => _wallpaper ?? throw new InvalidOperationException("Wallpaper changes are disabled.");
    private void SetEmbeddedWindowIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return;
            _windowIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (_windowIcon is not null)
                _appWindow.SetIcon(Win32Interop.GetIconIdFromIcon(_windowIcon.Handle));
        }
        catch
        {
            _windowIcon?.Dispose();
            _windowIcon = null;
        }
    }
    private void CompleteSmokeTest() { _exitRequested = true; Close(); }
    internal void ShowAndActivate()
    {
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _appWindow.Show(); Activate();
    }
    private void ExitFromTray() { _exitRequested = true; Close(); }
    private AppWindow GetAppWindow() => AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
    private async Task InitializeAsync()
    {
        if (_startupOptions.RunsLegacyProfileMigration) MigrateLegacyProfiles();
        var loadResult = await SetupStore.LoadOrCreateAsync();
        _canSaveSetupState = loadResult.CanSave;
        _setupManager = new SetupManager(loadResult.State);
        _profiles = Setups.ActiveSetup.MonitorProfiles;
        await RefreshAsync();
        if (_startupOptions.StartsPersistedSlideshows)
            await Task.WhenAll(_displayList.Select(InitializePersistedSlideshowAsync));
        if (loadResult.Failure is not null)
            ShowSetupStatus("Setup data needs attention · existing file preserved", showUndo: false);
    }
    private async Task InitializePersistedSlideshowAsync(MonitorInfo display)
    {
        var setupId = Setups.ActiveSetup.Id;
        var profile = Profile(display);
        if (profile.Mode != WallpaperMode.Slideshow || !profile.Enabled) return;
        var folder = profile.SlideshowFolderPath ?? "";
        using var operation = _folderScans.Begin(display.Id);
        try
        {
            var result = await ImageCatalog.ScanAsync(folder, operation.Token);
            if (!operation.IsCurrent || Setups.ActiveSetup.Id != setupId || profile.Mode != WallpaperMode.Slideshow || profile.SlideshowFolderPath != folder) return;
            if (result.IsAvailable && result.Files.Count > 0)
                await StartSlideshowAsync(display, profile, result.Files, setupId);
            else if (_selected?.Id == display.Id)
                ShowScanResult(result);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
    }
    private void MigrateLegacyProfiles()
    {
        Directory.CreateDirectory(SettingsFolder);
        var paneProfiles = Path.Combine(SettingsFolder, "profiles.json");
        var legacyProfiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallflow", "profiles.json");
        if (!File.Exists(paneProfiles) && File.Exists(legacyProfiles)) File.Copy(legacyProfiles, paneProfiles);
    }
    private async Task RefreshAsync()
    {
        _folderScans.CancelAll();
        _displayList = (await Monitors.GetMonitorsAsync()).ToList();
        Setups.ReconcileActiveMonitors(_displayList);
        Setups.ReconcileMonitorAliases(_displayList);
        _profiles = Setups.ActiveSetup.MonitorProfiles;
        await SaveSetupStateAsync();
        UpdateSetupPresentation();
        if (_displayList.Count == 0)
        {
            RenderMonitors();
            _selected = null;
            Editor.IsHitTestVisible = false;
            Editor.Opacity = 0.55;
            SelectedName.Text = "No displays detected";
            MonitorNameButton.IsEnabled = false;
            SelectedDetails.Text = "Refresh when a display is connected.";
        }
        else
        {
            var selected = _displayList.FirstOrDefault(display => display.Id == _selected?.Id) ?? _displayList[0];
            SelectMonitor(selected);
        }
    }
    private async Task<bool> SaveSetupStateAsync(bool showFailure = true)
    {
        if (!_canSaveSetupState || _setupManager is null) return false;
        try
        {
            await SetupStore.SaveAsync(Setups.State);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (showFailure) ShowSetupStatus("Setup changes are in memory · save unavailable", showUndo: false);
            return false;
        }
    }

    private void UpdateSetupPresentation()
    {
        if (_setupManager is null) return;
        var setup = Setups.ActiveSetup;
        ActiveSetupName.Text = setup.Name;
        AutomationProperties.SetName(SetupSwitcherButton, $"Open setups. Active setup: {setup.Name}");
        var connected = CountConnectedDisplays(setup);
        var saved = setup.MonitorProfiles.Count;
        var slideshows = setup.MonitorProfiles.Count(profile => profile.Mode == WallpaperMode.Slideshow && profile.Enabled);
        var displayText = saved > connected
            ? $"{connected} of {saved} displays connected"
            : $"{connected} {(connected == 1 ? "display" : "displays")}";
        SetupSummary.Text = $"{displayText} · {slideshows} {(slideshows == 1 ? "slideshow" : "slideshows")} configured";
    }

    private int CountConnectedDisplays(WallpaperSetup setup)
        => _displayList.Count(display => setup.MonitorProfiles.Any(profile =>
            string.Equals(profile.MonitorId, display.Id, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(profile.MonitorDevicePath) &&
             string.Equals(profile.MonitorDevicePath, display.DeviceName, StringComparison.OrdinalIgnoreCase))));

    private void SetupSwitcherButton_Click(object sender, RoutedEventArgs e)
    {
        if (SetupPanel.Visibility == Visibility.Visible) CloseSetupPanel();
        else OpenSetupPanel();
    }

    private void OpenSetupPanel()
    {
        RenderSetupCards();
        SetupPanelDismissLayer.Visibility = Visibility.Visible;
        SetupPanel.Visibility = Visibility.Visible;
        SetupPanel.UpdateLayout();
        EnsureSetupPanelComposition();
        AttachButtonMotionTree(SetupPanel);
        if (SetupCardsPanel.Children.FirstOrDefault() is Grid firstCardHost &&
            firstCardHost.Children.OfType<Button>().FirstOrDefault() is Button firstCard)
            firstCard.Focus(FocusState.Programmatic);
        if (!DispatcherQueue.TryEnqueue(PlaceSetupPanelAtDefaultAnchor))
            PlaceSetupPanelAtDefaultAnchor();
    }

    private void PlaceSetupPanelAtDefaultAnchor()
    {
        if (SetupPanel.Visibility != Visibility.Visible) return;
        var anchor = SetupSwitcherButton.TransformToVisual(RootGrid).TransformPoint(
            new Windows.Foundation.Point(0, SetupSwitcherButton.ActualHeight + 6));
        SetSetupPanelPosition(anchor.X, anchor.Y);
    }

    private void CloseSetupPanel()
    {
        _draggingSetupPanel = false;
        SetupPanelDragHeader.ReleasePointerCaptures();
        SetupPanel.Visibility = Visibility.Collapsed;
        SetupPanelDismissLayer.Visibility = Visibility.Collapsed;
        SetupSwitcherButton.Focus(FocusState.Programmatic);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || SetupPanel.Visibility != Visibility.Visible) return;
        CloseSetupPanel();
        e.Handled = true;
    }

    private async void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_editingMonitorName && !IsWithinElement(source, MonitorNameEditor))
            await CompleteMonitorNameEditAsync(save: true, restoreNeutralFocus: false);
        if (_editingSetupNameId is not null && !IsWithinElement(source, _setupNameEditor))
            await CompleteSetupNameEditAsync(save: true, restoreNeutralFocus: false);
    }

    private static bool IsWithinElement(DependencyObject? source, DependencyObject? target)
    {
        if (source is null || target is null) return false;
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, target)) return true;
        return false;
    }

    private void SetupPanelDismissLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CloseSetupPanel();
        e.Handled = true;
    }

    private void SetupPanelDragHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);
        if (!point.Properties.IsLeftButtonPressed || IsInteractiveSetupHeaderSource(e.OriginalSource as DependencyObject) || !SetupPanelDragHeader.CapturePointer(e.Pointer)) return;
        _draggingSetupPanel = true;
        _setupPanelPointerId = e.Pointer.PointerId;
        _setupPanelDragStart = point.Position;
        _setupPanelDragOrigin = GetSetupPanelPosition();
        e.Handled = true;
    }

    private bool IsInteractiveSetupHeaderSource(DependencyObject? source)
    {
        for (var current = source; current is not null && current != SetupPanelDragHeader; current = VisualTreeHelper.GetParent(current))
            if (current is Control) return true;
        return false;
    }

    private void SetupPanelDragHeader_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSetupPanel || e.Pointer.PointerId != _setupPanelPointerId) return;
        var point = e.GetCurrentPoint(RootGrid);
        SetSetupPanelPosition(
            _setupPanelDragOrigin.X + point.Position.X - _setupPanelDragStart.X,
            _setupPanelDragOrigin.Y + point.Position.Y - _setupPanelDragStart.Y);
        e.Handled = true;
    }

    private void SetupPanelDragHeader_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndSetupPanelDrag(e);

    private void SetupPanelDragHeader_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => EndSetupPanelDrag(e);

    private void SetupPanelDragHeader_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _setupPanelPointerId) _draggingSetupPanel = false;
    }

    private void EndSetupPanelDrag(PointerRoutedEventArgs e)
    {
        if (!_draggingSetupPanel || e.Pointer.PointerId != _setupPanelPointerId) return;
        _draggingSetupPanel = false;
        SetupPanelDragHeader.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SetupPanel.Visibility == Visibility.Visible)
        {
            var position = GetSetupPanelPosition();
            SetSetupPanelPosition(position.X, position.Y);
        }
    }

    private void SetupPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSetupPanelShadowSize();
        if (SetupPanel.Visibility == Visibility.Visible)
        {
            var position = GetSetupPanelPosition();
            SetSetupPanelPosition(position.X, position.Y);
        }
    }

    private FloatingPanelPosition GetSetupPanelPosition()
    {
        EnsureSetupPanelComposition();
        var visual = ElementCompositionPreview.GetElementVisual(SetupPanel);
        visual.Properties.TryGetVector3("Translation", out var translation);
        return new(translation.X, translation.Y);
    }

    private void SetSetupPanelPosition(double x, double y)
    {
        var position = FloatingPanelPlacement.Clamp(
            x,
            y,
            SetupPanel.ActualWidth > 0 ? SetupPanel.ActualWidth : SetupPanel.Width,
            SetupPanel.ActualHeight > 0 ? SetupPanel.ActualHeight : SetupPanel.MaxHeight,
            RootGrid.ActualWidth,
            RootGrid.ActualHeight);
        EnsureSetupPanelComposition();
        ElementCompositionPreview.GetElementVisual(SetupPanel).Properties.InsertVector3(
            "Translation", new Vector3((float)position.X, (float)position.Y, 0));
    }

    private void EnsureSetupPanelComposition()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(SetupPanel, true);
        if (_setupPanelShadowVisual is not null)
        {
            UpdateSetupPanelShadowSize();
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(SetupPanel).Compositor;
        _setupPanelShadowGeometry = compositor.CreateRoundedRectangleGeometry();
        _setupPanelShadowGeometry.CornerRadius = new Vector2(20, 20);
        _setupPanelShadowVisual = compositor.CreateSpriteVisual();
        _setupPanelShadowVisual.Brush = compositor.CreateColorBrush(Colors.Black);
        _setupPanelShadowVisual.Clip = compositor.CreateGeometricClip(_setupPanelShadowGeometry);
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Colors.Black;
        shadow.BlurRadius = 26;
        shadow.Opacity = 0.28f;
        shadow.Offset = new Vector3(0, 8, 0);
        _setupPanelShadowVisual.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(SetupPanelShadowHost, _setupPanelShadowVisual);
        UpdateSetupPanelShadowSize();
    }

    private void UpdateSetupPanelShadowSize()
    {
        if (_setupPanelShadowVisual is null || _setupPanelShadowGeometry is null) return;
        var size = new Vector2((float)SetupPanel.ActualWidth, (float)SetupPanel.ActualHeight);
        _setupPanelShadowVisual.Size = size;
        _setupPanelShadowGeometry.Size = size;
    }

    private void RenderSetupCards()
    {
        SetupCardsPanel.Children.Clear();
        if (_setupManager is null) return;
        foreach (var setup in Setups.State.Setups) SetupCardsPanel.Children.Add(CreateSetupCard(setup));
        if (SetupPanel.Visibility == Visibility.Visible)
        {
            SetupCardsPanel.UpdateLayout();
            AttachButtonMotionTree(SetupCardsPanel);
            var position = GetSetupPanelPosition();
            SetSetupPanelPosition(position.X, position.Y);
        }
    }

    private FrameworkElement CreateSetupCard(WallpaperSetup setup)
    {
        var isActive = setup.Id == Setups.State.ActiveSetupId;
        var connected = CountConnectedDisplays(setup);
        var saved = setup.MonitorProfiles.Count;
        var slideshows = setup.MonitorProfiles.Count(profile => profile.Mode == WallpaperMode.Slideshow && profile.Enabled);
        var summary = connected < saved
            ? $"{connected} of {saved} displays connected"
            : slideshows == 0
                ? $"{saved} {(saved == 1 ? "display" : "displays")} · static"
                : $"{saved} {(saved == 1 ? "display" : "displays")} · {slideshows} {(slideshows == 1 ? "slideshow" : "slideshows")}";

        var content = new Grid { ColumnSpacing = 13, Margin = new Thickness(0, 0, 34, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(122) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var topology = CreateMiniTopology(setup);
        Grid.SetColumn(topology, 0);
        content.Children.Add(topology);
        var editingName = string.Equals(_editingSetupNameId, setup.Id, StringComparison.Ordinal);
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var nameDisplay = new TextBlock { Text = setup.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190, Opacity = editingName ? 0 : 1 };
        text.Children.Add(nameDisplay);
        text.Children.Add(new TextBlock { Text = summary, FontSize = 12, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        if (isActive)
        {
            var active = new SymbolIcon(Symbol.Accept) { Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(active, 2);
            content.Children.Add(active);
        }

        var card = new Button
        {
            Content = content,
            Tag = setup.Id,
            Style = (Style)Application.Current.Resources["SetupCardStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = isActive ? (Brush)Application.Current.Resources["SetupActiveCardBackgroundBrush"] : (Brush)Application.Current.Resources["SetupCardBackgroundBrush"]
        };
        AutomationProperties.SetName(card, $"{setup.Name}. {summary}{(isActive ? ". Active setup" : string.Empty)}");
        card.Click += SetupCard_Click;

        var more = new Button
        {
            Content = new SymbolIcon(Symbol.More),
            Style = (Style)Application.Current.Resources["SetupMoreButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        AutomationProperties.SetName(more, $"More options for {setup.Name}");
        ToolTipService.SetToolTip(more, "Setup options");
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename", Icon = new SymbolIcon(Symbol.Edit) };
        rename.Click += async (_, _) => await BeginSetupNameEditAsync(setup.Id);
        var duplicate = new MenuFlyoutItem { Text = "Duplicate", Icon = new SymbolIcon(Symbol.Copy) };
        duplicate.Click += async (_, _) => await DuplicateSetupAsync(setup.Id);
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete), IsEnabled = Setups.State.Setups.Count > 1 };
        delete.Click += async (_, _) => await DeleteSetupAsync(setup.Id);
        menu.Items.Add(rename);
        menu.Items.Add(duplicate);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        more.Flyout = menu;

        var host = new Grid();
        host.Children.Add(card);
        host.Children.Add(more);
        if (editingName)
        {
            var editor = new TextBox
            {
                Text = setup.Name,
                MaxLength = SetupManager.MaximumSetupNameLength,
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Style = (Style)Application.Current.Resources["InlineRenameTextBoxStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(149, 10, 52, 0)
            };
            editor.KeyDown += SetupNameEditor_KeyDown;
            editor.LostFocus += SetupNameEditor_LostFocus;
            host.Children.Add(editor);
            _setupNameEditor = editor;
            _setupNameDisplay = nameDisplay;
            _setupNameFocusTarget = card;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_setupNameEditor, editor) || editor.Visibility != Visibility.Visible) return;
                editor.Focus(FocusState.Programmatic);
                editor.SelectAll();
            });
        }
        return host;
    }

    private Canvas CreateMiniTopology(WallpaperSetup setup)
    {
        const double canvasWidth = 112;
        const double canvasHeight = 48;
        var canvas = new Canvas { Width = canvasWidth, Height = canvasHeight, VerticalAlignment = VerticalAlignment.Center };
        var profiles = setup.MonitorProfiles.Take(5).Select((profile, index) => new
        {
            Profile = profile,
            X = profile.DisplayWidth > 0 ? profile.DisplayX : index * 1920,
            Y = profile.DisplayHeight > 0 ? profile.DisplayY : 0,
            Width = profile.DisplayWidth > 0 ? profile.DisplayWidth : 1920,
            Height = profile.DisplayHeight > 0 ? profile.DisplayHeight : 1080
        }).ToArray();
        if (profiles.Length == 0)
        {
            canvas.Children.Add(new Border { Width = 42, Height = 25, CornerRadius = new CornerRadius(4), Background = CreateTopologyGradient(), BorderBrush = (Brush)Application.Current.Resources["BorderBrush"], BorderThickness = new Thickness(1) });
            Canvas.SetLeft(canvas.Children[0], 35);
            Canvas.SetTop(canvas.Children[0], 11);
            return canvas;
        }

        var minX = profiles.Min(item => item.X);
        var minY = profiles.Min(item => item.Y);
        var maxX = profiles.Max(item => item.X + item.Width);
        var maxY = profiles.Max(item => item.Y + item.Height);
        var xPositions = profiles.Select(item => item.X).Distinct().Order().ToArray();
        var yPositions = profiles.Select(item => item.Y).Distinct().Order().ToArray();
        const double monitorGap = 5;
        var horizontalGapWidth = Math.Max(0, xPositions.Length - 1) * monitorGap;
        var verticalGapHeight = Math.Max(0, yPositions.Length - 1) * monitorGap;
        var scale = Math.Min(
            Math.Max(1, canvasWidth - 6 - horizontalGapWidth) / Math.Max(1, maxX - minX),
            Math.Max(1, canvasHeight - 6 - verticalGapHeight) / Math.Max(1, maxY - minY));
        var layoutWidth = (maxX - minX) * scale + horizontalGapWidth;
        var layoutHeight = (maxY - minY) * scale + verticalGapHeight;
        foreach (var item in profiles)
        {
            var connected = _displayList.Any(display =>
                string.Equals(display.Id, item.Profile.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(display.DeviceName, item.Profile.MonitorDevicePath, StringComparison.OrdinalIgnoreCase));
            var screen = new Border
            {
                Width = Math.Max(9, item.Width * scale),
                Height = Math.Max(8, item.Height * scale),
                CornerRadius = new CornerRadius(3),
                Background = CreateTopologyGradient(),
                BorderBrush = connected ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                Opacity = connected ? 1 : 0.58
            };
            var horizontalRank = Array.IndexOf(xPositions, item.X);
            var verticalRank = Array.IndexOf(yPositions, item.Y);
            Canvas.SetLeft(screen, (canvasWidth - layoutWidth) / 2 + (item.X - minX) * scale + horizontalRank * monitorGap);
            Canvas.SetTop(screen, (canvasHeight - layoutHeight) / 2 + (item.Y - minY) * scale + verticalRank * monitorGap);
            canvas.Children.Add(screen);
            _ = LoadTopologyThumbnailAsync(item.Profile, screen);
        }
        if (setup.MonitorProfiles.Count > profiles.Length)
        {
            var remaining = new TextBlock { Text = $"+{setup.MonitorProfiles.Count - profiles.Length}", FontSize = 10, Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"] };
            Canvas.SetLeft(remaining, 94);
            Canvas.SetTop(remaining, 34);
            canvas.Children.Add(remaining);
        }
        return canvas;
    }

    private static async Task LoadTopologyThumbnailAsync(MonitorWallpaperProfile profile, Border screen)
    {
        var candidates = profile.Mode == WallpaperMode.Slideshow
            ? new[] { profile.LastWallpaperPath }
            : new[] { profile.LastWallpaperPath, profile.StaticImagePath };
        string? sourcePath = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !ImageCatalog.IsSupported(candidate)) continue;
            bool exists;
            try { exists = await Task.Run(() => File.Exists(candidate)); }
            catch { exists = false; }
            if (!exists) continue;
            sourcePath = candidate;
            break;
        }
        if (sourcePath is null) return;

        var fallbackBackground = screen.Background;
        try
        {
            screen.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 9, 10, 13));
            var bitmap = new BitmapImage { DecodePixelWidth = 160 };
            var image = new Image
            {
                Source = bitmap,
                Stretch = PreviewStretch(profile.FitMode),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var previewLayer = new Grid
            {
                Clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, 0, screen.Width, screen.Height)
                }
            };
            previewLayer.Children.Add(image);
            bitmap.ImageFailed += (_, _) =>
            {
                screen.Child = null;
                screen.Background = fallbackBackground;
            };
            screen.Child = previewLayer;
            bitmap.UriSource = new Uri(sourcePath);
        }
        catch
        {
            screen.Child = null;
            screen.Background = fallbackBackground;
        }
    }

    private static Stretch PreviewStretch(WallpaperFit fitMode) => fitMode switch
    {
        WallpaperFit.Fill => Stretch.UniformToFill,
        WallpaperFit.Fit => Stretch.Uniform,
        WallpaperFit.Stretch => Stretch.Fill,
        WallpaperFit.Center => Stretch.None,
        _ => Stretch.UniformToFill
    };

    private static Brush CreateTopologyGradient() => new LinearGradientBrush
    {
        StartPoint = new(.1, 0),
        EndPoint = new(.9, 1),
        GradientStops =
        {
            new GradientStop { Color = ColorHelper.FromArgb(255, 45, 59, 94) },
            new GradientStop { Color = ColorHelper.FromArgb(255, 92, 66, 112), Offset = 1 }
        }
    };

    private async void SetupCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string setupId }) return;
        CloseSetupPanel();
        await SwitchSetupAsync(setupId, offerUndo: true);
    }

    private async Task SwitchSetupAsync(string setupId, bool offerUndo, bool force = false, string? completionMessage = null)
    {
        if (_setupManager is null || (!force && setupId == Setups.ActiveSetup.Id)) return;
        CommitEditorToProfile();
        await SaveSetupStateAsync();
        var previousSetupId = Setups.ActiveSetup.Id;
        var destinationName = Setups.Find(setupId).Name;
        try
        {
            var completed = await _setupSwitches.RunLatestAsync(async token =>
            {
                _folderScans.CancelAll();
                await StopAllSessionsAsync();
                token.ThrowIfCancellationRequested();
                var destination = Setups.Activate(setupId);
                var resolution = Setups.ReconcileActiveMonitors(_displayList);
                _profiles = destination.MonitorProfiles;
                _selected = null;
                UpdateSetupPresentation();
                if (_displayList.Count > 0) SelectMonitor(_displayList[0]); else RenderMonitors();
                var unavailable = await ApplyActiveSetupAsync(destination.Id, resolution, token);
                token.ThrowIfCancellationRequested();
                await SaveSetupStateAsync();
                UpdateSetupPresentation();
                RenderMonitors();
                var feedback = completionMessage ?? $"{destinationName} applied";
                if (unavailable > 0) feedback += $" · {unavailable} source{(unavailable == 1 ? string.Empty : "s")} unavailable";
                if (offerUndo && Setups.State.Setups.Any(setup => setup.Id == previousSetupId))
                {
                    _undoTracker.Offer(previousSetupId, destination.Id);
                    ShowSetupStatus(feedback, showUndo: true);
                }
                else
                {
                    _undoTracker.Clear();
                    ShowSetupStatus(feedback, showUndo: false);
                }
            });
            if (!completed) RenderSetupCards();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ShowSetupStatus($"{destinationName} could not be fully applied", showUndo: false);
        }
    }

    private async Task<int> ApplyActiveSetupAsync(string setupId, SetupMonitorResolution resolution, CancellationToken token)
    {
        var unavailable = 0;
        foreach (var match in resolution.Matches)
        {
            token.ThrowIfCancellationRequested();
            var profile = match.Profile;
            if (!profile.Enabled) continue;
            if (profile.Mode == WallpaperMode.Static)
            {
                if (!File.Exists(profile.StaticImagePath) || !ImageCatalog.IsSupported(profile.StaticImagePath!))
                {
                    unavailable++;
                    continue;
                }
                try
                {
                    await Wallpaper.SetWallpaperAsync(match.Monitor.Id, profile.StaticImagePath!, profile.FitMode, token);
                    profile.LastWallpaperPath = profile.StaticImagePath;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsRecoverableSourceFailure(ex)) { unavailable++; }
                continue;
            }

            var folder = profile.SlideshowFolderPath ?? string.Empty;
            using var scan = _folderScans.Begin(match.Monitor.Id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scan.Token);
            ImageCatalogScanResult result;
            try { result = await ImageCatalog.ScanAsync(folder, linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            if (!scan.IsCurrent || Setups.ActiveSetup.Id != setupId || profile.SlideshowFolderPath != folder)
                throw new OperationCanceledException(token);
            if (!result.IsAvailable || result.Files.Count == 0)
            {
                unavailable++;
                continue;
            }
            await StartSlideshowAsync(match.Monitor, profile, result.Files, setupId);
        }
        return unavailable;
    }

    private static bool IsRecoverableSourceFailure(Exception exception)
        => exception is WallpaperItemException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private async void NewSetup_Click(object sender, RoutedEventArgs e)
    {
        CloseSetupPanel();
        if (_setupManager is null) return;
        var nameBox = new TextBox
        {
            PlaceholderText = "Setup name",
            MaxLength = SetupManager.MaximumSetupNameLength,
            Style = (Style)Application.Current.Resources["JellyTextBoxStyle"]
        };
        var useCurrent = new RadioButton { Content = "Use current setup", IsChecked = true, GroupName = "SetupCreationMode" };
        var startFresh = new RadioButton { Content = "Start fresh", GroupName = "SetupCreationMode" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameBox);
        content.Children.Add(useCurrent);
        content.Children.Add(startFresh);
        var dialog = CreateDialog("New Setup", content, "Create");
        dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text)) return;
            args.Cancel = true;
            nameBox.Header = "Name is required";
            nameBox.Focus(FocusState.Programmatic);
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();

        CommitEditorToProfile();
        await _setupSwitches.RunLatestAsync(async token =>
        {
            _folderScans.CancelAll();
            await StopAllSessionsAsync();
            token.ThrowIfCancellationRequested();
            var setup = useCurrent.IsChecked == true
                ? Setups.CreateFromCurrent(name)
                : Setups.CreateFresh(name, _displayList);
            Setups.ReconcileActiveMonitors(_displayList);
            _profiles = setup.MonitorProfiles;
            _selected = null;
            await SaveSetupStateAsync();
            UpdateSetupPresentation();
            if (_displayList.Count > 0) SelectMonitor(_displayList[0]); else RenderMonitors();
            _undoTracker.Clear();
            ShowSetupStatus($"{setup.Name} created", showUndo: false);
        });
    }

    private async Task DuplicateSetupAsync(string setupId)
    {
        var duplicate = Setups.Duplicate(setupId);
        await SaveSetupStateAsync();
        RenderSetupCards();
        ShowSetupStatus($"{duplicate.Name} created", showUndo: false);
    }

    private async Task BeginSetupNameEditAsync(string setupId)
    {
        if (_editingMonitorName)
            await CompleteMonitorNameEditAsync(save: true, restoreNeutralFocus: false);
        if (_editingSetupNameId is not null)
            await CompleteSetupNameEditAsync(save: true, restoreNeutralFocus: false);
        _editingSetupNameId = setupId;
        RenderSetupCards();
    }

    private async void SetupNameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CompleteSetupNameEditAsync(save: true, restoreNeutralFocus: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            await CompleteSetupNameEditAsync(save: false, restoreNeutralFocus: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Tab)
        {
            e.Handled = true;
            await CompleteSetupNameEditAsync(save: true, restoreNeutralFocus: true);
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
        }
    }

    private async void SetupNameEditor_LostFocus(object sender, RoutedEventArgs e)
        => await CompleteSetupNameEditAsync(save: true, restoreNeutralFocus: false);

    private async Task CompleteSetupNameEditAsync(bool save, bool restoreNeutralFocus)
    {
        if (_editingSetupNameId is null) return;
        var setupId = _editingSetupNameId;
        var editor = _setupNameEditor;
        var display = _setupNameDisplay;
        var focusTarget = _setupNameFocusTarget;
        _editingSetupNameId = null;
        _setupNameEditor = null;
        _setupNameDisplay = null;
        _setupNameFocusTarget = null;
        if (editor is not null) editor.Visibility = Visibility.Collapsed;
        if (display is not null) display.Opacity = 1;
        if (restoreNeutralFocus) focusTarget?.Focus(FocusState.Pointer);

        if (!save || editor is null) return;
        if (!Setups.Rename(setupId, editor.Text))
        {
            ShowSetupStatus("Setup name cannot be empty", showUndo: false);
            return;
        }

        var setup = Setups.Find(setupId);
        if (display is not null) display.Text = setup.Name;
        await SaveSetupStateAsync();
        UpdateSetupPresentation();
        ShowSetupStatus("Setup renamed", showUndo: false);
    }

    private async Task DeleteSetupAsync(string setupId)
    {
        var setup = Setups.Find(setupId);
        var deletingActive = setup.Id == Setups.ActiveSetup.Id;
        if (deletingActive)
        {
            CloseSetupPanel();
            var confirmation = CreateDialog("Delete active setup?", new TextBlock
            {
                Text = $"{setup.Name} will be removed from Pane. Wallpaper files and folders will not be deleted.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360
            }, "Delete");
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        }
        CommitEditorToProfile();
        var deleteResult = Setups.Delete(setupId);
        if (!deleteResult.ActiveSetupChanged)
        {
            await SaveSetupStateAsync();
            RenderSetupCards();
            ShowSetupStatus($"{setup.Name} deleted", showUndo: false);
            return;
        }
        _undoTracker.Clear();
        await SwitchSetupAsync(deleteResult.ActiveSetupId, offerUndo: false, force: true);
    }

    private ContentDialog CreateDialog(string title, object content, string primaryText) => new()
    {
        XamlRoot = RootGrid.XamlRoot,
        Title = title,
        Content = content,
        PrimaryButtonText = primaryText,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary
    };

    private async void SetupUndo_Click(object sender, RoutedEventArgs e)
    {
        HideSetupStatus();
        if (!_undoTracker.TryTake(out var setupId) || _setupManager is null || !Setups.State.Setups.Any(setup => setup.Id == setupId)) return;
        var setupName = Setups.Find(setupId).Name;
        await SwitchSetupAsync(setupId, offerUndo: false, completionMessage: $"Restored {setupName}");
    }

    private void ShowSetupStatus(string message, bool showUndo)
    {
        var wasVisible = SetupStatusSurface.Visibility == Visibility.Visible;
        var fromOpacity = wasVisible ? SetupStatusSurface.Opacity : 0;
        var fromY = wasVisible ? SetupStatusTransform.TranslateY : 8;
        SetupStatusText.Text = message;
        SetupUndoButton.Visibility = showUndo ? Visibility.Visible : Visibility.Collapsed;
        SetupStatusSurface.Visibility = Visibility.Visible;
        AnimateStatus(fromOpacity, 1, fromY, 0, 170, null);
        _statusTimer ??= CreateStatusTimer();
        _statusTimer.Stop();
        _statusTimer.Interval = TimeSpan.FromSeconds(showUndo ? 4.5 : 3);
        _statusTimer.Start();
    }

    private DispatcherQueueTimer CreateStatusTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideSetupStatus();
        return timer;
    }

    private void HideSetupStatus()
    {
        _statusTimer?.Stop();
        if (SetupStatusSurface.Visibility != Visibility.Visible) return;
        AnimateStatus(SetupStatusSurface.Opacity, 0, SetupStatusTransform.TranslateY, 8, 130,
            () => SetupStatusSurface.Visibility = Visibility.Collapsed);
    }

    private void AnimateStatus(double fromOpacity, double toOpacity, double fromY, double toY, int milliseconds, Action? completed)
    {
        var previous = _statusStoryboard;
        _statusStoryboard = null;
        previous?.Stop();
        SetupStatusSurface.Opacity = fromOpacity;
        SetupStatusTransform.TranslateY = fromY;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = TimeSpan.FromMilliseconds(milliseconds), EnableDependentAnimation = true, EasingFunction = ease };
        var translate = new DoubleAnimation { From = fromY, To = toY, Duration = TimeSpan.FromMilliseconds(milliseconds), EnableDependentAnimation = true, EasingFunction = ease };
        Storyboard.SetTarget(opacity, SetupStatusSurface);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(translate, SetupStatusTransform);
        Storyboard.SetTargetProperty(translate, "TranslateY");
        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translate);
        _statusStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_statusStoryboard, storyboard)) return;
            _statusStoryboard = null;
            completed?.Invoke();
        };
        storyboard.Begin();
    }

    private sealed class ButtonMotionState
    {
        public bool IsPointerOver { get; set; }
        public bool IsPressed { get; set; }
    }

    private void AttachButtonMotionTree(DependencyObject root)
    {
        if (root is Button button) EnableButtonMotion(button);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            AttachButtonMotionTree(VisualTreeHelper.GetChild(root, index));
    }

    private void EnableButtonMotion(Button button)
    {
        if (_buttonMotionStates.TryGetValue(button, out _)) return;
        _buttonMotionStates.Add(button, new ButtonMotionState());
        ElementCompositionPreview.SetIsTranslationEnabled(button, true);
        UpdateButtonMotionCenter(button);
        button.SizeChanged += ButtonMotion_SizeChanged;
        button.PointerEntered += ButtonMotion_PointerEntered;
        button.PointerExited += ButtonMotion_PointerExited;
        button.PointerPressed += ButtonMotion_PointerPressed;
        button.PointerReleased += ButtonMotion_PointerReleased;
        button.PointerCanceled += ButtonMotion_PointerCanceled;
        button.PointerCaptureLost += ButtonMotion_PointerCaptureLost;
    }

    private static void UpdateButtonMotionCenter(Button button)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new Vector3((float)(button.ActualWidth / 2), (float)(button.ActualHeight / 2), 0);
    }

    private void ButtonMotion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Button button) UpdateButtonMotionCenter(button);
    }

    private void ButtonMotion_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !_buttonMotionStates.TryGetValue(button, out var state)) return;
        state.IsPointerOver = true;
        if (!state.IsPressed) AnimateButtonMotion(button, -1.25f, 1.005f, 130);
    }

    private void ButtonMotion_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !_buttonMotionStates.TryGetValue(button, out var state)) return;
        state.IsPointerOver = false;
        if (!state.IsPressed) AnimateButtonMotion(button, 0, 1, 120);
    }

    private void ButtonMotion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !_buttonMotionStates.TryGetValue(button, out var state)) return;
        state.IsPressed = true;
        AnimateButtonMotion(button, 0, 0.98f, 95);
    }

    private void ButtonMotion_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button || !_buttonMotionStates.TryGetValue(button, out var state)) return;
        state.IsPressed = false;
        AnimateButtonMotion(button, state.IsPointerOver ? -1.25f : 0, state.IsPointerOver ? 1.005f : 1, 120);
    }

    private void ButtonMotion_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => ResetButtonMotion(sender);

    private void ButtonMotion_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => ResetButtonMotion(sender);

    private void ResetButtonMotion(object sender)
    {
        if (sender is not Button button || !_buttonMotionStates.TryGetValue(button, out var state)) return;
        state.IsPressed = false;
        AnimateButtonMotion(button, state.IsPointerOver ? -1.25f : 0, state.IsPointerOver ? 1.005f : 1, 120);
    }

    private static void AnimateButtonMotion(Button button, float translateY, float scale, int milliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        var translation = compositor.CreateScalarKeyFrameAnimation();
        translation.InsertKeyFrame(1, translateY, easing);
        translation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Translation.Y", translation);
        var scaling = compositor.CreateVector3KeyFrameAnimation();
        scaling.InsertKeyFrame(1, new Vector3(scale, scale, 1), easing);
        scaling.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Scale", scaling);
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
        var glow = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(20), Background = selected ? new SolidColorBrush(ColorHelper.FromArgb(24, 124, 140, 255)) : new SolidColorBrush(Colors.Transparent) };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var frame = new Border { Height = height, Margin = new Thickness(10, 4, 10, 0), Padding = new Thickness(5), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 35, 38, 47)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(selected ? ColorHelper.FromArgb(42, 124, 140, 255) : ColorHelper.FromArgb(20, 255, 255, 255)) };
        var screen = new Grid { Background = new LinearGradientBrush { StartPoint = new(.1, 0), EndPoint = new(.9, 1), GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(255, 33, 45, 74) }, new GradientStop { Color = ColorHelper.FromArgb(255, 87, 62, 105), Offset = 1 } } } };
        var path = profile.LastWallpaperPath ?? profile.StaticImagePath;
        if (File.Exists(path))
        {
            var previewStretch = PreviewStretch(profile.FitMode);
            screen.Children.Add(new Image { Source = new BitmapImage(new Uri(path)), Stretch = previewStretch, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        screen.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Top, Height = height * .22, Background = new LinearGradientBrush { StartPoint = new(.5, 0), EndPoint = new(.5, 1), GradientStops = { new GradientStop { Color = ColorHelper.FromArgb(18, 255, 255, 255) }, new GradientStop { Color = Colors.Transparent, Offset = 1 } } } }); frame.Child = screen;
        stack.Children.Add(frame); stack.Children.Add(new Border { Width = 12, Height = 13, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 51, 60)), HorizontalAlignment = HorizontalAlignment.Center }); stack.Children.Add(new Border { Width = Math.Min(68, width * .4), Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 55, 58, 68)), HorizontalAlignment = HorizontalAlignment.Center });
        var label = new TextBlock { Text = MonitorDisplayName(display) + (display.IsPrimary ? "  •  PRIMARY" : ""), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) }; stack.Children.Add(label);
        container.Children.Add(glow); container.Children.Add(stack); container.PointerPressed += (_, _) => SelectMonitor(display); return container;
    }
    private MonitorWallpaperProfile Profile(MonitorInfo info) => _profiles.First(profile => string.Equals(profile.MonitorId, info.Id, StringComparison.OrdinalIgnoreCase));
    private void SelectMonitor(MonitorInfo monitor)
    {
        _selected = monitor; var profile = Profile(monitor); Editor.IsHitTestVisible = true; Editor.Opacity = 1; SelectedName.Text = MonitorDisplayName(monitor); MonitorNameButton.IsEnabled = true;
        AutomationProperties.SetName(MonitorNameButton, $"Rename {SelectedName.Text}");
        var displayDetails = new List<string> { monitor.Resolution };
        if (monitor.IsPrimary) displayDetails.Add("Primary display");
        displayDetails.Add(monitor.RefreshRate > 1 ? $"{monitor.RefreshRate} Hz" : "Refresh rate unavailable");
        SelectedDetails.Text = string.Join("  •  ", displayDetails);
        _updatingEditor = true;
        ModeToggle.IsOn = profile.Mode == WallpaperMode.Slideshow;
        ImagePathText.Text = profile.StaticImagePath ?? "No image selected";
        FolderPathText.Text = profile.SlideshowFolderPath ?? "No folder selected";
        ShuffleToggle.IsOn = profile.ShuffleEnabled;
        LoopToggle.IsOn = profile.LoopEnabled;
        FitBox.SelectedIndex = (int)profile.FitMode;
        TransitionBox.SelectedIndex = profile.Transition == TransitionKind.None ? 0 : 1;
        var intervalMinutes = Math.Max(1, (int)Math.Round(profile.SlideshowInterval.TotalMinutes));
        IntervalBox.SelectedIndex = Enumerable.Range(0, IntervalBox.Items.Count)
            .FirstOrDefault(index => IntervalBox.Items[index] is ComboBoxItem item && item.Tag?.ToString() == intervalMinutes.ToString(), 2);
        _updatingEditor = false;
        SetPreview(profile.LastWallpaperPath ?? profile.StaticImagePath); ValidationText.Text = ""; RenderMonitors();
    }
    private string MonitorDisplayName(MonitorInfo monitor)
        => _setupManager is null ? monitor.FriendlyName : Setups.GetMonitorDisplayName(monitor, _displayList);

    private async void MonitorNameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _editingMonitorName) return;
        if (_editingSetupNameId is not null)
            await CompleteSetupNameEditAsync(save: true, restoreNeutralFocus: false);
        _editingMonitorName = true;
        _monitorNameEditTarget = _selected;
        MonitorNameEditor.Text = MonitorDisplayName(_selected);
        MonitorNameButton.Visibility = Visibility.Collapsed;
        MonitorNameEditor.Visibility = Visibility.Visible;
        MonitorNameEditor.Focus(FocusState.Programmatic);
        MonitorNameEditor.SelectAll();
    }

    private async void MonitorNameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CompleteMonitorNameEditAsync(save: true, restoreNeutralFocus: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            await CompleteMonitorNameEditAsync(save: false, restoreNeutralFocus: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Tab)
        {
            e.Handled = true;
            await CompleteMonitorNameEditAsync(save: true, restoreNeutralFocus: true);
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
        }
    }

    private async void MonitorNameEditor_LostFocus(object sender, RoutedEventArgs e)
        => await CompleteMonitorNameEditAsync(save: true, restoreNeutralFocus: false);

    private async Task CompleteMonitorNameEditAsync(bool save, bool restoreNeutralFocus)
    {
        if (!_editingMonitorName) return;
        _editingMonitorName = false;
        var target = _monitorNameEditTarget;
        _monitorNameEditTarget = null;
        MonitorNameEditor.Visibility = Visibility.Collapsed;
        MonitorNameButton.Visibility = Visibility.Visible;
        if (restoreNeutralFocus) MonitorNameButton.Focus(FocusState.Pointer);
        if (save && target is not null && _setupManager is not null)
        {
            if (!Setups.SetMonitorAlias(target, MonitorNameEditor.Text))
            {
                ShowSetupStatus($"Monitor names can contain at most {SetupManager.MaximumMonitorAliasLength} characters", showUndo: false);
            }
            else
            {
                await SaveSetupStateAsync();
                RenderMonitors();
            }
        }
        if (_selected is not null)
        {
            SelectedName.Text = MonitorDisplayName(_selected);
            AutomationProperties.SetName(MonitorNameButton, $"Rename {SelectedName.Text}");
        }
    }
    private void SetPreview(string? path) { var exists = File.Exists(path); EmptyPreview.Visibility = exists ? Visibility.Collapsed : Visibility.Visible; WallpaperPreview.Stretch = _selected is null ? Stretch.Uniform : PreviewStretch(Profile(_selected).FitMode); WallpaperPreview.Source = exists ? new BitmapImage(new Uri(path!)) : null; }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { CommitEditorToProfile(); await RefreshAsync(); }
    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderMonitors();
    private void Identify_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "Display identification overlays are planned; refresh and topology detection are active now.";
    }
    private void ModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        StaticPanel.Visibility = ModeToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        SlideshowPanel.Visibility = ModeToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!ModeToggle.IsOn && _selected is not null) _folderScans.Cancel(_selected.Id);
        if (!_updatingEditor) CommitEditorToProfile();
    }
    private void FitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEditor || _selected is null || FitBox.SelectedIndex < 0) return;
        Profile(_selected).FitMode = (WallpaperFit)FitBox.SelectedIndex;
        WallpaperPreview.Stretch = PreviewStretch(Profile(_selected).FitMode);
        RenderMonitors();
    }
    private void EditorSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingEditor) CommitEditorToProfile();
    }
    private void EditorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingEditor) CommitEditorToProfile();
    }
    private void CommitEditorToProfile()
    {
        if (_updatingEditor || _selected is null || !_profiles.Any(profile => profile.MonitorId == _selected.Id)) return;
        var profile = Profile(_selected);
        profile.Mode = ModeToggle.IsOn ? WallpaperMode.Slideshow : WallpaperMode.Static;
        profile.ShuffleEnabled = ShuffleToggle.IsOn;
        profile.LoopEnabled = LoopToggle.IsOn;
        if (FitBox.SelectedIndex >= 0) profile.FitMode = (WallpaperFit)FitBox.SelectedIndex;
        profile.Transition = TransitionBox.SelectedIndex == 0 ? TransitionKind.None : TransitionKind.SoftFade;
        if (IntervalBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var minutes))
            profile.SlideshowInterval = TimeSpan.FromMinutes(minutes);
    }
    private async void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; var picker = new FileOpenPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync(); if (file is null) return; Profile(_selected).StaticImagePath = file.Path; ImagePathText.Text = file.Path; SetPreview(file.Path); ValidationText.Text = ""; await SaveSetupStateAsync();
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var monitor = _selected;
        var picker = new FolderPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var setupId = Setups.ActiveSetup.Id;
        var profile = Profile(monitor); profile.SlideshowFolderPath = folder.Path;
        await SaveSetupStateAsync();
        if (_selected?.Id == monitor.Id) { FolderPathText.Text = folder.Path; ValidationText.Text = "Scanning folder…"; }
        using var operation = _folderScans.Begin(monitor.Id);
        try
        {
            var result = await ImageCatalog.ScanAsync(folder.Path, operation.Token);
            if (!operation.IsCurrent || Setups.ActiveSetup.Id != setupId || profile.SlideshowFolderPath != folder.Path || _selected?.Id != monitor.Id) return;
            ShowScanResult(result);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        CommitEditorToProfile();
        var monitor = _selected;
        var setupId = Setups.ActiveSetup.Id;
        var profile = Profile(monitor);
        try
        {
            await SaveSetupStateAsync();
            if (profile.Mode == WallpaperMode.Static)
            {
                _folderScans.Cancel(monitor.Id);
                if (!File.Exists(profile.StaticImagePath) || !ImageCatalog.IsSupported(profile.StaticImagePath!)) { ValidationText.Text = "Choose a supported wallpaper image first."; return; }
                await Wallpaper.SetWallpaperAsync(monitor.Id, profile.StaticImagePath!, profile.FitMode); profile.LastWallpaperPath = profile.StaticImagePath;
            }
            else
            {
                var folder = profile.SlideshowFolderPath ?? "";
                using var operation = _folderScans.Begin(monitor.Id);
                ValidationText.Text = "Scanning folder…";
                var result = await ImageCatalog.ScanAsync(folder, operation.Token);
                if (!operation.IsCurrent || Setups.ActiveSetup.Id != setupId || profile.Mode != WallpaperMode.Slideshow || profile.SlideshowFolderPath != folder) return;
                if (!result.IsAvailable || result.Files.Count == 0) { ShowScanResult(result); return; }
                await StartSlideshowAsync(monitor, profile, result.Files, setupId);
            }
            await SaveSetupStateAsync(); ValidationText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 116, 221, 164)); ValidationText.Text = profile.Mode == WallpaperMode.Static ? "Wallpaper applied" : "Slideshow started"; UpdateSetupPresentation(); RenderMonitors();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ValidationText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 123, 134)); ValidationText.Text = ex.Message; }
    }
    private void ShowScanResult(ImageCatalogScanResult result)
    {
        ValidationText.Foreground = new SolidColorBrush(result.IsAvailable ? ColorHelper.FromArgb(255, 183, 190, 209) : ColorHelper.FromArgb(255, 255, 123, 134));
        ValidationText.Text = result.Failure?.Message ?? (result.Files.Count == 0
            ? "This folder doesn't contain any supported images."
            : result.WasTruncated ? $"{result.Files.Count:N0} supported images (folder limit reached)" : $"{result.Files.Count:N0} supported images");
    }
    private async Task StartSlideshowAsync(MonitorInfo monitor, MonitorWallpaperProfile profile, IReadOnlyList<string> files, string setupId)
    {
        if (_sessions.Remove(monitor.Id, out var old)) await old.DisposeAsync();
        var session = new SlideshowSession(monitor, profile, new WallpaperTransitionService(Wallpaper), files);
        session.WallpaperChanged += (_, path) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_setupManager?.ActiveSetup.Id != setupId) return;
            if (_selected?.Id == monitor.Id) SetPreview(path);
            RenderMonitors();
        });
        _sessions[monitor.Id] = session; session.Start();
    }
    private async Task StopAllSessionsAsync()
    {
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        foreach (var session in sessions) session.Stop();
        foreach (var session in sessions) await session.DisposeAsync();
    }
}
