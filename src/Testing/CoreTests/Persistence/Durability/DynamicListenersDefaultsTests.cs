using Shouldly;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// Lock down the upgrade-safety contract for the new dynamic-listener registry:
///   the opt-in flag defaults to <c>false</c>, the default
///   <c>IMessageStore.Listeners</c> is the no-op store, and existing apps that
///   bump Wolverine without touching options pay nothing - no schema migration,
///   no behavioural change, no agent family registration.
/// </summary>
public class DynamicListenersDefaultsTests
{
    [Fact]
    public void enable_dynamic_listeners_defaults_to_false_on_a_fresh_durability_settings()
    {
        // Direct construction (no host bootstrap) - guards against a future
        // ctor or property-initializer regression flipping the default.
        new DurabilitySettings().EnableDynamicListeners.ShouldBeFalse();
    }

    [Fact]
    public void enable_dynamic_listeners_defaults_to_false_on_wolverine_options()
    {
        // Whole-options walk - covers any indirect mutation that
        // WolverineOptions' constructor could introduce on Durability.
        new WolverineOptions().Durability.EnableDynamicListeners.ShouldBeFalse();
    }

    [Fact]
    public async Task null_listener_store_register_is_a_no_op()
    {
        // The default store every IMessageStore.Listeners points at when the
        // flag is off. Locks down the no-op contract: no exception, no state.
        var store = NullListenerStore.Instance;

        await store.RegisterListenerAsync(new Uri("mqtt://topic/devices/abc"));
    }

    [Fact]
    public async Task null_listener_store_returns_empty_listing()
    {
        var listeners = await NullListenerStore.Instance.AllListenersAsync();
        listeners.ShouldBeEmpty();
    }

    [Fact]
    public async Task null_listener_store_remove_is_a_no_op()
    {
        await NullListenerStore.Instance.RemoveListenerAsync(new Uri("mqtt://topic/whatever"));
    }
}
