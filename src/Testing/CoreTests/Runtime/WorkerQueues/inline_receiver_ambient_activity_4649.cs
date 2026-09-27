using System.Diagnostics;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4649. An Inline endpoint has no worker-pool boundary: <see cref="InlineReceiver" /> runs the
/// pipeline on whatever task is driving the listener, and that task is a bare <c>Task.Run</c> that
/// captured whatever <see cref="Activity.Current" /> was set when the listener STARTED. The receive and
/// execution spans fall back to <c>Activity.Current</c> for an envelope with no <c>ParentId</c>, so
/// every such message handled on that listener was parented under the one stale span -- the Solo-mode
/// startup <c>wolverine_node_assignments</c> activity, or the request that started the listener.
/// </summary>
/// <remarks>
/// The receivers for the other modes hand off to a <c>Block&lt;Envelope&gt;</c>, which clears the ambient
/// activity per item since jasperfx#904. Inline never touches a block, so the receiver itself has to
/// drop the ambient value before it starts the receive span.
/// </remarks>
public class inline_receiver_ambient_activity_4649 : IDisposable
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;
    private readonly ActivitySource theSource = new("CoreTests.gh-4649");
    private readonly List<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public inline_receiver_ambient_activity_4649()
    {
        // Without a listener that samples, StartActivity returns null: there is no startup activity
        // to inherit and no receive activity to inspect, and every fact below passes vacuously.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine" || source.Name == theSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_stopped)
                {
                    _stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);

        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));

        thePipeline
            .InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<Activity>())
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _listener.Dispose();
        theSource.Dispose();
    }

    private static Envelope parentlessEnvelope()
    {
        // The case the fallback in WolverineTracing.StartEnvelopeActivity exists for: a message that
        // was sent from untraced code, or by a non-Wolverine producer.
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = null;
        return envelope;
    }

    private Activity receiveSpanFor(Envelope envelope)
    {
        lock (_stopped)
        {
            // The tag is written as the Guid itself, not its string form.
            return _stopped.Single(a =>
                a.OperationName == "receive" && Equals(a.GetTagItem(WolverineTracing.MessagingMessageId), envelope.Id));
        }
    }

    /// <summary>
    /// The GH-4649 repro: the listener loop starts on <c>Task.Run</c> while a startup activity is
    /// current, that activity ends, and only then does a message arrive on the loop.
    /// </summary>
    [Fact]
    public async Task a_parentless_message_is_not_parented_under_the_activity_current_when_the_loop_started()
    {
        var envelope = parentlessEnvelope();
        var arrived = new TaskCompletionSource();

        Task loop;
        Activity startup;
        using (startup = theSource.StartActivity("startup")!)
        {
            // The guard for the guard: nothing ambient, nothing inherited, nothing proven.
            startup.ShouldNotBeNull();

            // What BackgroundReceiveLoop.Start() does when a listener is started under
            // wolverine_node_assignments: the loop captures this context and outlives the activity.
            loop = Task.Run(async () =>
            {
                await arrived.Task;
                await theReceiver.ReceivedAsync(theListener, envelope);
            }, TestContext.Current.CancellationToken);
        }

        arrived.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var receive = receiveSpanFor(envelope);
        receive.Parent.ShouldBeNull("the receive span inherited the activity current when the listener loop started");
        receive.TraceId.ShouldNotBe(startup.TraceId);
    }

    /// <summary>
    /// The control: an envelope that DOES carry a parent id is still parented by it, so the clear
    /// cannot have reached into the explicit-parent path.
    /// </summary>
    [Fact]
    public async Task a_message_with_a_parent_id_keeps_that_parent()
    {
        var envelope = ObjectMother.Envelope();
        using var upstream = theSource.StartActivity("upstream")!;
        upstream.ShouldNotBeNull();
        envelope.ParentId = upstream.Id;

        await theReceiver.ReceivedAsync(theListener, envelope);

        receiveSpanFor(envelope).ParentId.ShouldBe(upstream.Id);
    }

    /// <summary>
    /// The second control: the pipeline runs under the receive span it is handed, so dropping the
    /// ambient value must not break the nesting inside one message.
    /// </summary>
    [Fact]
    public async Task the_pipeline_still_runs_inside_the_receive_span()
    {
        Activity? currentInsidePipeline = null;
        thePipeline
            .InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<Activity>())
            .Returns(ci =>
            {
                currentInsidePipeline = Activity.Current;
                return Task.CompletedTask;
            });

        var envelope = parentlessEnvelope();
        await theReceiver.ReceivedAsync(theListener, envelope);

        currentInsidePipeline.ShouldBeSameAs(receiveSpanFor(envelope));
    }

    /// <summary>
    /// Dropping the receiver's ambient activity does not reach back into the loop that awaited it, which
    /// is the claim the approach rests on: an <see cref="AsyncLocal{T}" /> write inside an awaited method
    /// does not propagate to the awaiter.
    /// </summary>
    [Fact]
    public async Task the_loops_own_activity_survives()
    {
        using var ambient = theSource.StartActivity("ambient")!;
        ambient.ShouldNotBeNull();

        await theReceiver.ReceivedAsync(theListener, parentlessEnvelope());

        Activity.Current.ShouldBeSameAs(ambient);
    }
}
