using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime.Agents;

// GH-3027: a single host listening on the same logical queue name across multiple transports
// (e.g. a "critterwatch" queue on both Rabbit and SQS) produced colliding leader-pinned /
// exclusive listener agent Uris — they were composed from only the family scheme + EndpointName,
// with no transport identifier — so the family's `.ToDictionary(e => e.Uri)` threw
// "An item with the same key has already been added." Including the endpoint's transport scheme
// in the agent Uri keeps the keys distinct.
public class listener_agent_uri_includes_transport_scheme
{
    private static Endpoint EndpointFor(string scheme)
        => new SchemeEndpoint(new Uri($"{scheme}://queue/critterwatch")) { EndpointName = "critterwatch" };

    [Fact]
    public void leader_pinned_agent_uri_carries_the_transport_scheme()
    {
        var rabbit = new LeaderPinnedListenerAgent(EndpointFor("rabbitmq"), null!);
        var sqs = new LeaderPinnedListenerAgent(EndpointFor("sqs"), null!);

        rabbit.Uri.ShouldBe(new Uri("wolverine-leader-listener://rabbitmq/critterwatch"));
        sqs.Uri.ShouldBe(new Uri("wolverine-leader-listener://sqs/critterwatch"));
        rabbit.Uri.ShouldNotBe(sqs.Uri);
    }

    [Fact]
    public void exclusive_agent_uri_carries_the_transport_scheme()
    {
        var rabbit = new ExclusiveListenerAgent(EndpointFor("rabbitmq"), null!);
        var sqs = new ExclusiveListenerAgent(EndpointFor("sqs"), null!);

        rabbit.Uri.ShouldBe(new Uri("wolverine-listener://rabbitmq/critterwatch"));
        sqs.Uri.ShouldBe(new Uri("wolverine-listener://sqs/critterwatch"));
        rabbit.Uri.ShouldNotBe(sqs.Uri);
    }

    [Fact]
    public void same_named_listeners_across_transports_do_not_collide_in_the_family_dictionary()
    {
        // Mirrors LeaderPinnedListenerFamily / ExclusiveListenerFamily ctors, which build the agent
        // set with `.ToDictionary(e => e.Uri)`. Pre-fix this threw on the second same-named endpoint.
        Endpoint[] endpoints = [EndpointFor("rabbitmq"), EndpointFor("sqs"), EndpointFor("azure-service-bus")];

        Should.NotThrow(() => endpoints
            .Select(e => new LeaderPinnedListenerAgent(e, null!))
            .ToDictionary(e => e.Uri));

        Should.NotThrow(() => endpoints
            .Select(e => new ExclusiveListenerAgent(e, null!))
            .ToDictionary(e => e.Uri));
    }

    // Minimal concrete Endpoint whose Uri scheme is controllable — the only thing the listener
    // agent ctors read besides EndpointName.
    private sealed class SchemeEndpoint : Endpoint
    {
        public SchemeEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime)
            => throw new NotSupportedException();
    }
}
