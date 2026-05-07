using NSubstitute;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents.DynamicListeners;

/// <summary>
/// Unit coverage for <see cref="DynamicListenerAgentFamily"/> — verifies the
/// family reads from <see cref="IListenerStore"/> on every assignment cycle
/// (no cached snapshot), produces stable agent URIs that round-trip back to
/// the original listener URI, and balances them via
/// <c>assignments.DistributeEvenly(Scheme)</c>. Pairs with the
/// <see cref="DynamicListenerUriEncodingTests"/> round-trip coverage.
/// </summary>
public class DynamicListenerAgentFamilyTests
{
    private readonly MockWolverineRuntime _runtime;
    private readonly IListenerStore _listenerStore;
    private readonly DynamicListenerAgentFamily _family;

    public DynamicListenerAgentFamilyTests()
    {
        _runtime = new MockWolverineRuntime();
        _listenerStore = Substitute.For<IListenerStore>();
        _runtime.Storage.Listeners.Returns(_listenerStore);

        _family = new DynamicListenerAgentFamily(_runtime);
    }

    [Fact]
    public void scheme_is_the_dynamic_listener_uri_scheme()
    {
        // The family's Scheme is the dictionary key in NodeAgentController's
        // _agentFamilies; it must match the scheme of every agent URI the
        // family hands out so dispatch from a URI back to the family works.
        _family.Scheme.ShouldBe(DynamicListenerUriEncoding.SchemeName);
    }

    [Fact]
    public async Task all_known_agents_returns_empty_when_store_is_empty()
    {
        _listenerStore.AllListenersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Uri>>(Array.Empty<Uri>()));

        var agents = await _family.AllKnownAgentsAsync();

        agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task all_known_agents_projects_each_listener_uri_through_encoder()
    {
        var listenerA = new Uri("mqtt://broker/topic-a");
        var listenerB = new Uri("mqtt://broker/topic-b");
        _listenerStore.AllListenersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Uri>>(new[] { listenerA, listenerB }));

        var agents = await _family.AllKnownAgentsAsync();

        agents.Count.ShouldBe(2);
        agents.ShouldContain(DynamicListenerUriEncoding.ToAgentUri(listenerA));
        agents.ShouldContain(DynamicListenerUriEncoding.ToAgentUri(listenerB));
    }

    [Fact]
    public async Task all_known_agents_re_reads_store_on_every_call()
    {
        // The family deliberately doesn't cache — each cluster assignment
        // cycle must see freshly registered URIs without a host restart.
        _listenerStore.AllListenersAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<Uri>>(Array.Empty<Uri>()),
                Task.FromResult<IReadOnlyList<Uri>>(new[] { new Uri("mqtt://broker/new-topic") }));

        var first = await _family.AllKnownAgentsAsync();
        var second = await _family.AllKnownAgentsAsync();

        first.ShouldBeEmpty();
        second.Count.ShouldBe(1);
        await _listenerStore.Received(2).AllListenersAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task supported_agents_matches_all_known_agents()
    {
        // Every node has the family registered when EnableDynamicListeners is
        // on, so any node can run any of the listeners — supported set ==
        // known set. Transport-level "this node can't reach the broker" lives
        // in StartAsync, not in supported-agents filtering.
        _listenerStore.AllListenersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Uri>>(new[]
            {
                new Uri("mqtt://broker/topic-a"),
                new Uri("mqtt://broker/topic-b")
            }));

        var supported = await _family.SupportedAgentsAsync();
        var known = await _family.AllKnownAgentsAsync();

        supported.ShouldBe(known);
    }

    [Fact]
    public async Task build_agent_async_decodes_uri_and_constructs_dynamic_listener_agent()
    {
        var listener = new Uri("mqtt://broker/devices/foo/status");
        var agentUri = DynamicListenerUriEncoding.ToAgentUri(listener);

        var agent = await _family.BuildAgentAsync(agentUri, _runtime);

        agent.ShouldBeOfType<DynamicListenerAgent>();
        agent.Uri.ShouldBe(agentUri);
    }
}
