using System.Diagnostics;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class inline_receiver_activity_status : IDisposable
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;
    private readonly List<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public inline_receiver_activity_status()
    {
        // Capture the "receive" activity started inside InlineReceiver
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public async Task does_not_override_error_status_set_by_the_pipeline()
    {
        // Simulate a contained failure: the HandlerPipeline / Executor flags the
        // receive activity as Error but does NOT rethrow (the failure was handled
        // by an error-handling continuation such as dead-letter or discard).
        thePipeline
            .InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<Activity>())
            .Returns(ci =>
            {
                ci.Arg<Activity>().SetStatus(ActivityStatusCode.Error, "boom");
                return Task.CompletedTask;
            });

        await theReceiver.ReceivedAsync(theListener, ObjectMother.Envelope());

        var receive = _stopped.Single(a => a.OperationName == "receive");
        receive.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task marks_ok_on_the_happy_path()
    {
        thePipeline
            .InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<Activity>())
            .Returns(Task.CompletedTask);

        await theReceiver.ReceivedAsync(theListener, ObjectMother.Envelope());

        var receive = _stopped.Single(a => a.OperationName == "receive");
        receive.Status.ShouldBe(ActivityStatusCode.Ok);
    }
}
