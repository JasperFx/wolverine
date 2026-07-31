using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3746. A node with <see cref="DurabilitySettings.MessagingEnabled" /> set to false exists to run
/// event subscription (projection) agents and nothing else — the case is warming a bumped projection
/// version off to one side before it serves traffic. It must therefore advertise no agent family that
/// would make the leader hand it message-handling work, while still advertising the event
/// subscription agents it was stood up for.
/// </summary>
public class messaging_disabled_node
{
    private static NodeAgentController ControllerFor(bool messagingEnabled, params IAgentFamily[] families)
    {
        var options = new WolverineOptions { ApplicationAssembly = typeof(messaging_disabled_node).Assembly };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.Durability.MessagingEnabled = messagingEnabled;
        options.Durability.DurabilityAgentEnabled = false; // skip MessageStoreCollection wiring
        // Keep the recurring loop out of the way; the assertions are on construction alone.
        options.Durability.CheckAssignmentPeriod = 1.Hours();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        return new NodeAgentController(
            runtime,
            Substitute.For<INodeAgentPersistence>(),
            families,
            NullLogger<NodeAgentController>.Instance,
            CancellationToken.None);
    }

    [Fact]
    public void the_listener_families_are_not_registered()
    {
        // Without this the leader can pin an exclusive listener here, and a node whose read models
        // are still being built would start handling real messages.
        var controller = ControllerFor(messagingEnabled: false);

        controller.HasFamily(ExclusiveListenerFamily.SchemeName).ShouldBeFalse();
        controller.HasFamily(LeaderPinnedListenerFamily.SchemeName).ShouldBeFalse();
    }

    [Fact]
    public void the_listener_families_are_still_registered_by_default()
    {
        var controller = ControllerFor(messagingEnabled: true);

        controller.HasFamily(ExclusiveListenerFamily.SchemeName).ShouldBeTrue();
        controller.HasFamily(LeaderPinnedListenerFamily.SchemeName).ShouldBeTrue();
    }

    [Fact]
    public void event_subscription_families_are_still_registered()
    {
        // The whole point of the node: it is a full cluster member for projections. Turning messaging
        // off must not make it inert.
        var controller = ControllerFor(messagingEnabled: false, new StubFamily("event-subscriptions"));

        controller.HasFamily("event-subscriptions").ShouldBeTrue();
    }

    /// <summary>Stands in for the event subscription family a Marten/Polecat host injects.</summary>
    private sealed class StubFamily(string scheme) : IAgentFamily
    {
        public string Scheme { get; } = scheme;
        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync() => new([]);
        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync() => new([]);
        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => new();
    }
}
