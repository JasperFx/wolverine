using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
/// GH-5587. The flush after a transaction commits hands each outgoing envelope to its destination. A destination that
/// is not taking messages right now (a broker that is not accepting sends) must not hold up the envelopes for the
/// destinations that are. The flush itself still waits for every destination, and a destination still takes its own
/// envelopes one at a time and in order.
/// </summary>
public class flush_keeps_available_destinations_progressing_5587 : IAsyncDisposable
{
    private static readonly TimeSpan FailureBound = 10.Seconds();

    private readonly MockWolverineRuntime theRuntime = new();
    private readonly MessageContext theContext;
    private readonly List<SendingAgent> _agents = new();

    private readonly GatedSender theSaturatedSender = new(new Uri("stub://saturated"));
    private readonly GatedSender theAvailableSender = new(new Uri("stub://available"));

    public flush_keeps_available_destinations_progressing_5587()
    {
        theContext = new MessageContext(theRuntime);
    }

    public async ValueTask DisposeAsync()
    {
        theSaturatedSender.Open();
        foreach (var agent in _agents) await agent.DisposeAsync();
    }

    private SendingAgent agentFor(GatedSender sender)
    {
        var agent = new BufferedSendingAgent(NullLogger.Instance, Substitute.For<IMessageTracker>(), sender,
            theRuntime.Options.Durability, new StubEndpoint(sender.Destination));
        _agents.Add(agent);
        return agent;
    }

    private static Envelope envelopeFor(ISendingAgent agent)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = agent.Destination;
        envelope.Sender = agent;
        envelope.Status = EnvelopeStatus.Outgoing;
        return envelope;
    }

    // The envelopes of a handler whose transaction (Marten, EF Core...) has committed
    private async Task outgoingAsync(params Envelope[] envelopes)
    {
        await theContext.EnlistInOutboxAsync(Substitute.For<IEnvelopeTransaction>());

        // Wacky, but this adds them to the MessageContext's outstanding messages
        await theContext.As<IEnvelopeTransaction>().PersistOutgoingAsync(envelopes);
    }

    [Fact]
    public async Task a_destination_that_is_not_taking_messages_does_not_hold_up_another_destination()
    {
        var saturated = agentFor(theSaturatedSender);
        var available = agentFor(theAvailableSender);

        var saturated1 = envelopeFor(saturated);
        var available1 = envelopeFor(available);
        var saturated2 = envelopeFor(saturated);
        var available2 = envelopeFor(available);

        theSaturatedSender.Close();
        await outgoingAsync(saturated1, available1, saturated2, available2);

        var flushing = theContext.FlushOutgoingMessagesAsync();

        // Both of the available destination's envelopes get there while the saturated one is still stuck on its first
        await theAvailableSender.WaitForAsync(2).WaitAsync(FailureBound, TestContext.Current.CancellationToken);
        theAvailableSender.Sent.ShouldBe([available1.Id, available2.Id]);

        theSaturatedSender.Entered.ShouldBe(1);
        theSaturatedSender.Sent.ShouldBeEmpty();

        // The flush still waits for every destination
        flushing.IsCompleted.ShouldBeFalse();

        theSaturatedSender.Open();
        await flushing.WaitAsync(FailureBound, TestContext.Current.CancellationToken);

        // Never more of the saturated destination's envelopes at once, and in the order they were published
        theSaturatedSender.Sent.ShouldBe([saturated1.Id, saturated2.Id]);
        theSaturatedSender.MaximumInside.ShouldBe(1);
    }

    [Fact]
    public async Task more_saturated_destinations_than_available_ones_still_leave_the_available_ones_progressing()
    {
        var saturatedSenders = Enumerable.Range(0, 30)
            .Select(i => new GatedSender(new Uri($"stub://saturated-{i}")))
            .ToArray();
        foreach (var sender in saturatedSenders) sender.Close();

        try
        {
            var envelopes = saturatedSenders.Select(sender => envelopeFor(agentFor(sender))).ToList();
            var available = envelopeFor(agentFor(theAvailableSender));
            envelopes.Add(available);

            await outgoingAsync(envelopes.ToArray());

            var flushing = theContext.FlushOutgoingMessagesAsync();

            await theAvailableSender.WaitForAsync(1).WaitAsync(FailureBound, TestContext.Current.CancellationToken);
            theAvailableSender.Sent.ShouldBe([available.Id]);
            flushing.IsCompleted.ShouldBeFalse();

            foreach (var sender in saturatedSenders) sender.Open();
            await flushing.WaitAsync(FailureBound, TestContext.Current.CancellationToken);

            saturatedSenders.ShouldAllBe(x => x.Sent.Length == 1 && x.MaximumInside == 1);
        }
        finally
        {
            foreach (var sender in saturatedSenders) sender.Open();
        }
    }

    [Fact]
    public async Task every_destination_gets_its_envelopes_in_the_order_they_were_published()
    {
        var first = agentFor(theAvailableSender);
        var otherSender = new GatedSender(new Uri("stub://other"));
        var second = agentFor(otherSender);

        var envelopes = new[] { envelopeFor(first), envelopeFor(second), envelopeFor(first), envelopeFor(second) };
        await outgoingAsync(envelopes);

        await theContext.FlushOutgoingMessagesAsync().WaitAsync(FailureBound, TestContext.Current.CancellationToken);

        theAvailableSender.Sent.ShouldBe([envelopes[0].Id, envelopes[2].Id]);
        otherSender.Sent.ShouldBe([envelopes[1].Id, envelopes[3].Id]);
    }

    /// <summary>
    /// A transport sender that holds every send while it is closed, like a broker that is not accepting sends
    /// </summary>
    private class GatedSender : ISender
    {
        private TaskCompletionSource _open;
        private int _inside;
        private int _maximumInside;
        private int _entered;
        private readonly ConcurrentQueue<Guid> _sent = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = new();

        public GatedSender(Uri destination)
        {
            Destination = destination;
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _open.SetResult();
        }

        public Guid[] Sent => _sent.ToArray();
        public int Entered => Volatile.Read(ref _entered);
        public int MaximumInside => Volatile.Read(ref _maximumInside);

        public bool SupportsNativeScheduledSend => false;
        public Uri Destination { get; }
        public Task<bool> PingAsync() => Task.FromResult(true);

        public void Close()
        {
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Open() => _open.TrySetResult();

        public Task WaitForAsync(int count)
        {
            lock (_waiters)
            {
                if (_sent.Count >= count) return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, done));
                return done.Task;
            }
        }

        public async ValueTask SendAsync(Envelope envelope)
        {
            var inside = Interlocked.Increment(ref _inside);
            int maximum;
            while (inside > (maximum = Volatile.Read(ref _maximumInside)))
            {
                if (Interlocked.CompareExchange(ref _maximumInside, inside, maximum) == maximum) break;
            }

            Interlocked.Increment(ref _entered);

            try
            {
                await _open.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }

            lock (_waiters)
            {
                _sent.Enqueue(envelope.Id);
                foreach (var waiter in _waiters.Where(x => _sent.Count >= x.Count).ToArray())
                {
                    waiter.Done.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }
    }

    private class StubEndpoint(Uri uri) : Endpoint(uri, EndpointRole.Application)
    {
        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

        protected override bool supportsMode(EndpointMode mode) => true;
    }
}
