#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="IExtensionFeatures.Changed" /> follows ordinary .NET event rules over the host gate: a handler
///     added twice runs twice, and removing it twice leaves nothing on the gate.
/// </summary>
[NotInParallel]
public class ExtensionFeaturesTests
{
    [Test]
    public async Task AHandlerAddedTwice_IsGoneAfterTwoRemoves()
    {
        FaultRig rig = new();
        FakeGate gate = new();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(new ExtensionShellHub())
            .AddSingleton(rig.Faults)
            .AddSingleton<IFeatureGate>(gate)
            .BuildServiceProvider();
        IExtensionFeatures features = new ExtensionContext(new StubExtension(rig.Scope.FeatureId), services).Features;
        int calls = 0;
        void Handler() => calls++;

        features.Changed += Handler;
        features.Changed += Handler;
        gate.RaiseChanged();
        int whileSubscribedTwice = calls;

        features.Changed -= Handler;
        gate.RaiseChanged();
        int afterOneRemove = calls - whileSubscribedTwice;

        features.Changed -= Handler;
        calls = 0;
        gate.RaiseChanged();

        using (Assert.Multiple())
        {
            await Assert.That(whileSubscribedTwice).IsEqualTo(2);
            await Assert.That(afterOneRemove).IsEqualTo(1);
            await Assert.That(calls).IsEqualTo(0).Because("both subscriptions were removed");
            await Assert.That(gate.Subscribers).IsEqualTo(0);
        }
    }

    private sealed class FakeGate : IFeatureGate
    {
        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;

        public bool IsEnabled(string featureId) => true;

        private EventHandler? _changed;

        public event EventHandler? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public void RaiseChanged() => _changed?.Invoke(this, EventArgs.Empty);
    }
}
