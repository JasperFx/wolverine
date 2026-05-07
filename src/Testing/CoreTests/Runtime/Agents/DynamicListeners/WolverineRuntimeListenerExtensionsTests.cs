using NSubstitute;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime.Agents.DynamicListeners;

/// <summary>
/// Coverage for the public <see cref="WolverineRuntimeListenerExtensions"/>
/// surface — these are the methods user code calls (e.g. on an HTTP request
/// to add an MQTT broker), so the public contract needs to argument-validate
/// and pass straight through to <see cref="IListenerStore"/> without any
/// transformation. Most of the substance is exercised through the
/// <see cref="DynamicListenerAgentFamilyTests"/> suite — the tests here just
/// pin down the thin wrapper.
/// </summary>
public class WolverineRuntimeListenerExtensionsTests
{
    private readonly MockWolverineRuntime _runtime = new();
    private readonly IListenerStore _store = Substitute.For<IListenerStore>();

    public WolverineRuntimeListenerExtensionsTests()
    {
        _runtime.Storage.Listeners.Returns(_store);
    }

    [Fact]
    public async Task register_listener_async_delegates_to_store()
    {
        var uri = new Uri("mqtt://broker/topic");
        await _runtime.RegisterListenerAsync(uri);

        await _store.Received(1).RegisterListenerAsync(uri, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task remove_listener_async_delegates_to_store()
    {
        var uri = new Uri("mqtt://broker/topic");
        await _runtime.RemoveListenerAsync(uri);

        await _store.Received(1).RemoveListenerAsync(uri, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task all_registered_listeners_async_delegates_to_store()
    {
        var listed = new[]
        {
            new Uri("mqtt://broker/a"),
            new Uri("mqtt://broker/b")
        };
        _store.AllListenersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Uri>>(listed));

        var result = await _runtime.AllRegisteredListenersAsync();

        result.ShouldBe(listed);
    }

    [Fact]
    public void register_throws_argument_null_for_null_runtime()
    {
        IWolverineRuntime? runtime = null;
        var uri = new Uri("mqtt://broker/topic");
        Should.Throw<ArgumentNullException>(() => runtime!.RegisterListenerAsync(uri));
    }

    [Fact]
    public void register_throws_argument_null_for_null_uri()
    {
        Should.Throw<ArgumentNullException>(() => _runtime.RegisterListenerAsync(null!));
    }

    [Fact]
    public void remove_throws_argument_null_for_null_uri()
    {
        Should.Throw<ArgumentNullException>(() => _runtime.RemoveListenerAsync(null!));
    }
}
