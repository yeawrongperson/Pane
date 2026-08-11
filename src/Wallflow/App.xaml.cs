using Microsoft.UI.Xaml;
using Wallflow.Core;
namespace Wallflow;
public partial class App : Application
{
    private readonly SingleInstanceActivationCoordinator _activationCoordinator;
    private Window? _window;
    public App(SingleInstanceActivationCoordinator activationCoordinator)
    {
        _activationCoordinator = activationCoordinator;
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        var dispatcherQueue = window.DispatcherQueue;
        _activationCoordinator.RegisterWindowActivationHandler(() =>
            dispatcherQueue.TryEnqueue(window.ShowAndActivate));
        window.Activate();
    }
}
