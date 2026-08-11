using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wallflow.Core;

namespace Wallflow.Core.Tests;

[TestClass]
public sealed class SingleInstanceActivationCoordinatorTests
{
    [TestMethod]
    public void Instance_key_is_stable_and_application_specific()
        => Assert.AreEqual("Pane.SingleInstance", SingleInstanceActivationCoordinator.InstanceKey);

    [TestMethod]
    public void Activation_after_window_is_ready_runs_handler()
    {
        var coordinator = new SingleInstanceActivationCoordinator();
        var activations = 0;
        coordinator.RegisterWindowActivationHandler(() => activations++);

        coordinator.RequestWindowActivation();

        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public void Activation_before_window_is_ready_is_delivered_when_registered()
    {
        var coordinator = new SingleInstanceActivationCoordinator();
        var activations = 0;
        coordinator.RequestWindowActivation();

        coordinator.RegisterWindowActivationHandler(() => activations++);

        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public void Repeated_early_activations_are_safely_coalesced()
    {
        var coordinator = new SingleInstanceActivationCoordinator();
        var activations = 0;
        Parallel.For(0, 16, _ => coordinator.RequestWindowActivation());

        coordinator.RegisterWindowActivationHandler(() => Interlocked.Increment(ref activations));

        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public void Repeated_activations_after_readiness_are_each_delivered()
    {
        var coordinator = new SingleInstanceActivationCoordinator();
        var activations = 0;
        coordinator.RegisterWindowActivationHandler(() => Interlocked.Increment(ref activations));

        Parallel.For(0, 16, _ => coordinator.RequestWindowActivation());

        Assert.AreEqual(16, activations);
    }
}
