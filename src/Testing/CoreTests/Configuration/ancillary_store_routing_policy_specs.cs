using CoreTests.Runtime;
using JasperFx.CodeGeneration;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

public class ancillary_store_routing_policy_specs
{
    [Fact]
    public void route_messages_to_ancillary_store_marks_matching_handler_chains()
    {
        var options = new WolverineOptions();
        options.Policies.RouteMessagesToAncillaryStore<BulkStoreMarker>(typeof(BulkEvent));

        var matching = new HandlerChain(typeof(BulkEvent), new HandlerGraph());
        var other = new HandlerChain(typeof(RegularEvent), new HandlerGraph());

        foreach (var policy in options.Policies.OfType<IHandlerPolicy>())
        {
            policy.Apply([matching, other], new GenerationRules("Testing"), null!);
        }

        matching.AncillaryStoreType.ShouldBe(typeof(BulkStoreMarker));
        other.AncillaryStoreType.ShouldBeNull();
    }

    [Fact]
    public void route_messages_to_ancillary_store_stamps_matching_outgoing_envelopes()
    {
        var ancillaryStore = new NullMessageStore();
        var runtime = new MockWolverineRuntime([new AncillaryMessageStore(typeof(BulkStoreMarker), ancillaryStore)]);
        var context = new MessageContext(runtime);
        var options = new WolverineOptions();
        options.Policies.RouteMessagesToAncillaryStore<BulkStoreMarker>(typeof(BulkEvent));

        var matching = new Envelope(new BulkEvent());
        var other = new Envelope(new RegularEvent());

        foreach (var rule in options.MetadataRules)
        {
            rule.ApplyCorrelation(context, matching);
            rule.ApplyCorrelation(context, other);
        }

        matching.Store.ShouldBeSameAs(ancillaryStore);
        other.Store.ShouldBeNull();
    }
}

public record BulkEvent;

public record RegularEvent;

public interface BulkStoreMarker;
