using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Wallflow.Core;

namespace Wallflow;

public static class Program
{
    private static AppInstance? _primaryInstance;

    [STAThread]
    private static async Task Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var startupOptions = PaneStartupOptions.Parse(args);
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(startupOptions.InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            // Awaiting in C# WinUI's async Main lets COM dispatch continue instead of blocking the STA.
            await keyInstance.RedirectActivationToAsync(activationArgs);
            return;
        }

        var activationCoordinator = new SingleInstanceActivationCoordinator();
        _primaryInstance = keyInstance;
        _primaryInstance.Activated += (_, _) => activationCoordinator.RequestWindowActivation();

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(activationCoordinator, startupOptions);
        });
    }
}
