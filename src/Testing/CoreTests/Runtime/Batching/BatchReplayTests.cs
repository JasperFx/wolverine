using CoreTests.Acceptance;
using NSubstitute;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime.Batching;

public class BatchReplayTests
{
    private readonly Uri theDestination = new("local://items");

    // AgentForLocalQueue is declared as returning ISendingAgent, and BatchReplay casts it to
    // ILocalQueue, so the substitute has to be both.
    private readonly ISendingAgent theAgent = Substitute.For<ISendingAgent, ILocalQueue>();

    private readonly IWolverineRuntime theRuntime = Substitute.For<IWolverineRuntime>();

    private ILocalQueue theQueue => (ILocalQueue)theAgent;

    public BatchReplayTests()
    {
        var endpoints = Substitute.For<IEndpointCollection>();
        endpoints.AgentForLocalQueue(theDestination).Returns(theAgent);
        theRuntime.Endpoints.Returns(endpoints);
    }

    private async Task<Envelope> replay(Envelope batch, params object[] items)
    {
        await BatchReplay.EnqueueReducedBatchAsync(theRuntime, batch, items);

        var enqueued = theQueue.ReceivedCalls()
            .Where(x => x.GetMethodInfo().Name == nameof(ILocalQueue.EnqueueAsync))
            .Select(x => (Envelope)x.GetArguments()[0]!)
            .ToArray();

        return enqueued.ShouldHaveSingleItem();
    }

    private Envelope batchEnvelope(string? groupId, string? tenantId = null)
    {
        return new Envelope(new[] { new Item("one"), new Item("two") })
        {
            Destination = theDestination,
            MessageType = "item-array",
            GroupId = groupId,
            TenantId = tenantId
        };
    }

    [Fact]
    public async Task the_reduced_batch_keeps_the_group_id()
    {
        // GH-3867 — without this the probed batch has no group id, and an envelope with no group id
        // draws a RANDOM partition slot, so isolating a poison item scatters the survivors.
        var reduced = await replay(batchEnvelope("aaa"), new Item("one"));

        reduced.GroupId.ShouldBe("aaa");
    }

    [Fact]
    public async Task carries_the_rest_of_the_batch_identity_across()
    {
        var reduced = await replay(batchEnvelope("aaa", "tenant1"), new Item("one"));

        reduced.Destination.ShouldBe(theDestination);
        reduced.MessageType.ShouldBe("item-array");
        reduced.TenantId.ShouldBe("tenant1");
        reduced.Message.ShouldBeOfType<Item[]>().Length.ShouldBe(1);
    }

    [Fact]
    public async Task an_ungrouped_batch_stays_ungrouped()
    {
        var reduced = await replay(batchEnvelope(null), new Item("one"));

        reduced.GroupId.ShouldBeNull();
    }
}
