using Microsoft.UI.Xaml;
using Wallflow.Core;
namespace Wallflow;
public partial class App : Application
{
    private readonly SingleInstanceActivationCoordinator _activationCoordinator;
    private readonly PaneStartupOptions _startupOptions;
    private Window? _window;
    public App(SingleInstanceActivationCoordinator activationCoordinator, PaneStartupOptions startupOptions)
    {
        _activationCoordinator = activationCoordinator;
        _startupOptions = startupOptions;
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow(_startupOptions);
        _window = window;
        var dispatcherQueue = window.DispatcherQueue;
        _activationCoordinator.RegisterWindowActivationHandler(() =>
            dispatcherQueue.TryEnqueue(window.ShowAndActivate));
        window.Activate();
    }
}
