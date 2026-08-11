namespace Wallflow.Core;

public sealed class SingleInstanceActivationCoordinator
{
    public const string InstanceKey = "Pane.SingleInstance";

    private readonly object _gate = new();
    private Action? _activateWindow;
    private bool _activationPending;

    public void RequestWindowActivation()
    {
        Action? activateWindow;
        lock (_gate)
        {
            activateWindow = _activateWindow;
            if (activateWindow is null)
            {
                _activationPending = true;
                return;
            }
        }

        activateWindow();
    }

    public void RegisterWindowActivationHandler(Action activateWindow)
    {
        ArgumentNullException.ThrowIfNull(activateWindow);

        bool activatePending;
        lock (_gate)
        {
            if (_activateWindow is not null)
                throw new InvalidOperationException("A window activation handler is already registered.");

            _activateWindow = activateWindow;
            activatePending = _activationPending;
            _activationPending = false;
        }

        if (activatePending) activateWindow();
    }
}
