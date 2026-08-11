using JasperFx.CodeGeneration.Frames;
using Shouldly;

namespace Wolverine.Http.Tests.Marten;

// GH-3893: an HTTP chain whose only Marten shape is [WriteAggregate] gets a
// SaveChangesAsync postprocessor from the aggregate handler workflow, but the workflow
// never set IChain.IsTransactional. AutoApplyTransactions doesn't cover the gap either -
// MartenPersistenceFrameProvider.CanApply deliberately ignores [WriteAggregate] - so the
// flag and the generated code disagreed, and an IHttpPolicy keying on IsTransactional got
// a false negative.
public class write_aggregate_chain_is_transactional(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public void write_aggregate_only_chain_is_marked_as_transactional()
    {
        // POST /orders/ship3 is Ship3(ShipOrder command, [WriteAggregate] Order order) - no
        // IDocumentSession parameter, so [WriteAggregate] is the only thing attracting Marten
        var chain = HttpChains.ChainFor("POST", "/orders/ship3");
        chain.ShouldNotBeNull();

        // What codegen actually does with this chain
        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(global::Marten.IDocumentSession.SaveChangesAsync))
            .ShouldBeTrue();

        // ...and what the chain reports about itself. These have to agree.
        chain.IsTransactional.ShouldBeTrue();
    }
}
