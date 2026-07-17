using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Facade-abstraction gate for <see cref="IVoipClient"/> (HARD-E5). Every manager exposed on the
/// public client contract must be an interface, so a consumer can implement or fake the whole client
/// — including the managers that carry the runtime capability — for testing. Before E5 these were
/// concrete <c>sealed</c> classes and could not be substituted.
/// </summary>
public sealed class IVoipClientMockabilityTests
{
    [Fact]
    public void Every_manager_property_on_the_facade_is_an_interface()
    {
        foreach (var property in typeof(IVoipClient).GetProperties())
        {
            Assert.True(
                property.PropertyType.IsInterface,
                $"IVoipClient.{property.Name} exposes {property.PropertyType.Name}, which is not an interface — " +
                "the facade must stay mockable.");
        }
    }

    [Fact]
    public void A_capability_manager_can_be_substituted_by_a_fake()
    {
        // The runtime-capability manager is now an interface: a fake satisfies it and is usable
        // wherever the facade hands one back.
        ICallManager calls = new FakeCallManager();

        Assert.Empty(calls.Active);
        Assert.Null(calls.Find(CallId.New()));
    }

    private sealed class FakeCallManager : ICallManager
    {
        public event EventHandler<CallActivityEventArgs>? CallAdded { add { } remove { } }
        public event EventHandler<CallActivityEventArgs>? CallRemoved { add { } remove { } }
        public event EventHandler<CallStateChangedEventArgs>? CallStateChanged { add { } remove { } }

        public IReadOnlyCollection<ICall> Active => [];

        public ICall? Find(CallId id) => null;
    }
}
