using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5016. MessageContext.SendAllOutgoingMessagesInlineAsync() sends every outgoing envelope right now,
/// awaiting each one, whatever mode the destination endpoint is in -- and then deletes the outbox rows of
/// everything it sent in ONE batched call per message store.
/// </summary>
public class send_all_outgoing_messages_inline_5016 : IAsyncDisposable
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly MessageContext theContext;
    private readonly IEnvelopeTransaction theTransaction = Substitute.For<IEnvelopeTransaction>();
    private readonly ISender theSender = Substitute.For<ISender>();

    // The agent's own outbox. Sending inline must never hand anything to it
    private readonly IMessageOutbox theAgentOutbox = Substitute.For<IMessageOutbox>();
    private readonly List<SendingAgent> _agents = new();

    public send_all_outgoing_messages_inline_5016()
    {
        // GH-5038: the inline send's own failure handling, which is the ServerlessWithOutbox one. Outside that
        // mode a failed send goes to the destination's sending agent instead
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        theSender.Destination.Returns(new Uri("stub://one"));
        theContext = new MessageContext(theRuntime);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents) await agent.DisposeAsync();
    }

    private class StubEndpoint : Endpoint
    {
        public StubEndpoint() : base(new Uri("stub://one"), EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

        protected override bool supportsMode(EndpointMode mode) => true;
    }

    private SendingAgent durableAgent()
    {
        var agent = new DurableSendingAgent(theSender, theRuntime.Options.Durability, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), theAgentOutbox, new StubEndpoint());
        _agents.Add(agent);
        return agent;
    }

    private SendingAgent bufferedAgent()
    {
        var agent = new BufferedSendingAgent(NullLogger.Instance, Substitute.For<IMessageTracker>(), theSender,
            theRuntime.Options.Durability, new StubEndpoint());
        _agents.Add(agent);
        return agent;
    }

    private async Task<Envelope[]> outgoing(ISendingAgent agent, int count, bool externalTransaction = true)
    {
        if (externalTransaction)
        {
            // The handler's transaction (Marten, EF Core...) has already persisted the durable envelopes
            await theContext.EnlistInOutboxAsync(theTransaction);
        }

        var envelopes = Enumerable.Range(0, count).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Sender = agent;
            return envelope;
        }).ToArray();

        // Wacky, but this adds them to the MessageContext's outstanding messages
        await theContext.As<IEnvelopeTransaction>().PersistOutgoingAsync(envelopes);

        return envelopes;
    }

    [Fact]
    public async Task sends_every_envelope_inline_and_deletes_the_outbox_rows_in_one_batch()
    {
        var envelopes = await outgoing(durableAgent(), 3);

        await theContext.SendAllOutgoingMessagesInlineAsync();

        // Sent before the method returned, straight through the transport sender
        foreach (var envelope in envelopes)
        {
            await theSender.Received(1).SendAsync(envelope);
        }

        await theRuntime.Storage.Outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(x => x.Length == 3 && envelopes.All(x.Contains)));

        // Never one round trip per envelope, and never handed to the background agent
        await theRuntime.Storage.Outbox.DidNotReceive().DeleteOutgoingAsync(Arg.Any<Envelope>());
        await theAgentOutbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
        await theAgentOutbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope>());

        theContext.Outstanding.ShouldBeEmpty();
    }

    [Fact]
    public async Task one_batched_delete_per_message_store()
    {
        var envelopes = await outgoing(durableAgent(), 4);

        var store1 = Substitute.For<IMessageStore>();
        var store2 = Substitute.For<IMessageStore>();
        envelopes[0].Store = store1;
        envelopes[1].Store = store2;
        envelopes[2].Store = store1;
        envelopes[3].Store = store2;

        await theContext.SendAllOutgoingMessagesInlineAsync();

        await store1.Outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(x => x.Length == 2 && x.Contains(envelopes[0]) && x.Contains(envelopes[2])));
        await store2.Outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(x => x.Length == 2 && x.Contains(envelopes[1]) && x.Contains(envelopes[3])));

        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task outside_serverless_with_outbox_a_failed_send_goes_to_the_sending_agent_to_retry()
    {
        // GH-5038. Here the outbox row is owned by this live node, which no durability pass recovers
        theRuntime.Options.Durability.Mode = DurabilityMode.Balanced;

        var envelopes = await outgoing(durableAgent(), 2);
        var attempts = 0;
        theSender.SendAsync(envelopes[1]).Returns(_ =>
            Interlocked.Increment(ref attempts) == 1 ? throw new DivideByZeroException() : ValueTask.CompletedTask);

        await theContext.SendAllOutgoingMessagesInlineAsync();

        // The sending agent tried it again
        for (var i = 0; i < 100 && Volatile.Read(ref attempts) < 2; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Volatile.Read(ref attempts).ShouldBe(2);
        await theRuntime.Storage.Outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(x => x.Length == 1 && x.Contains(envelopes[0])));
    }

    [Fact]
    public async Task a_failed_send_is_left_in_the_outbox()
    {
        var envelopes = await outgoing(durableAgent(), 3);
        theSender.SendAsync(envelopes[1]).Throws(new DivideByZeroException());

        await theContext.SendAllOutgoingMessagesInlineAsync();

        // The other two still went
        await theSender.Received(1).SendAsync(envelopes[0]);
        await theSender.Received(1).SendAsync(envelopes[2]);

        await theRuntime.Storage.Outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(x => x.Length == 2 && !x.Contains(envelopes[1])));

        // Its row was persisted by the transaction already, so it is not stored again
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().StoreOutgoingAsync(Arg.Any<Envelope>(), default);
    }

    [Fact]
    public async Task nothing_to_delete_when_every_send_fails()
    {
        var envelopes = await outgoing(durableAgent(), 2);
        theSender.SendAsync(Arg.Any<Envelope>()).Throws(new DivideByZeroException());

        await theContext.SendAllOutgoingMessagesInlineAsync();

        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
        await theSender.Received(1).SendAsync(envelopes[0]);
        await theSender.Received(1).SendAsync(envelopes[1]);
    }

    [Fact]
    public async Task a_buffered_endpoint_is_sent_inline_too_and_has_no_outbox_rows_to_delete()
    {
        var envelopes = await outgoing(bufferedAgent(), 2);

        await theContext.SendAllOutgoingMessagesInlineAsync();

        // No background batching: both were sent before the method returned
        await theSender.Received(1).SendAsync(envelopes[0]);
        await theSender.Received(1).SendAsync(envelopes[1]);

        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task without_an_external_transaction_a_failed_durable_send_is_stored_for_recovery_as_owner_zero()
    {
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

        var envelopes = await outgoing(durableAgent(), 2, externalTransaction: false);
        theSender.SendAsync(envelopes[0]).Throws(new DivideByZeroException());

        await theContext.SendAllOutgoingMessagesInlineAsync();

        await theRuntime.Storage.Outbox.Received(1).StoreOutgoingAsync(envelopes[0], TransportConstants.AnyNode);

        // Nothing was ever persisted for the one that went, so there is no row to delete
        await theRuntime.Storage.Outbox.DidNotReceive().StoreOutgoingAsync(envelopes[1], Arg.Any<int>());
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task stamps_the_outgoing_owner_id()
    {
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        var envelopes = await outgoing(durableAgent(), 1);
        envelopes[0].OwnerId = 99;

        await theContext.SendAllOutgoingMessagesInlineAsync();

        envelopes[0].OwnerId.ShouldBe(TransportConstants.AnyNode);
        envelopes[0].Status.ShouldBe(EnvelopeStatus.Outgoing);
    }

    [Fact]
    public async Task counts_as_the_flush_of_this_context()
    {
        var envelopes = await outgoing(durableAgent(), 1);

        await theContext.SendAllOutgoingMessagesInlineAsync();
        await theContext.FlushOutgoingMessagesAsync();
        await theContext.SendAllOutgoingMessagesInlineAsync();

        await theSender.Received(1).SendAsync(envelopes[0]);
        await theRuntime.Storage.Outbox.Received(1).DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }
}
