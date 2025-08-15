using JasperFx.Core;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Xunit;

namespace SlowTests;

public class in_memory_scheduled_messages : ILocalQueue
{
    private readonly Dictionary<Guid, TaskCompletionSource<Envelope>>
        _callbacks = new();

    private readonly IList<Envelope> sent = new List<Envelope>();
    private readonly InMemoryScheduledJobProcessor theScheduledJobs;

    public in_memory_scheduled_messages()
    {
        theScheduledJobs = new InMemoryScheduledJobProcessor(this);
        sent.Clear();
        _callbacks.Clear();
    }

    public Uri Uri { get; }
    public Uri ReplyUri { get; }
    public Uri Destination { get; } = "local://delayed".ToUri();
    public Uri Alias { get; }

    public void Enqueue(Envelope envelope)
    {
        sent.Add(envelope);
        if (_callbacks.ContainsKey(envelope.Id))
        {
            _callbacks[envelope.Id].SetResult(envelope);
        }
    }

    public ValueTask EnqueueAsync(Envelope envelope)
    {
        sent.Add(envelope);
        if (_callbacks.ContainsKey(envelope.Id))
        {
            _callbacks[envelope.Id].SetResult(envelope);
        }

        return new ValueTask();
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DrainAsync()
    {
        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline Pipeline => null;

    public int QueueCount => 0;

    void IDisposable.Dispose()
    {
    }

    private Task<Envelope> waitForReceipt(Envelope envelope)
    {
        var source = new TaskCompletionSource<Envelope>();
        _callbacks.Add(envelope.Id, source);

        return source.Task;
    }

    [Fact]
    public async Task run_multiple_messages_through()
    {
        var env1 = ObjectMother.Envelope();
        var env2 = ObjectMother.Envelope();
        var env3 = ObjectMother.Envelope();

        var waiter1 = waitForReceipt(env1);
        var waiter2 = waitForReceipt(env2);
        var waiter3 = waitForReceipt(env3);

        theScheduledJobs.Enqueue(DateTime.UtcNow.AddHours(1), env1);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddSeconds(5), env2);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddHours(1), env3);

        await waiter2;

        waiter1.IsCompleted.ShouldBeFalse();
        waiter2.IsCompleted.ShouldBeTrue();
        waiter3.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void play_all()
    {
        var env1 = ObjectMother.Envelope();
        var env2 = ObjectMother.Envelope();
        var env3 = ObjectMother.Envelope();

        theScheduledJobs.Enqueue(DateTime.UtcNow.AddMinutes(1), env1);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddMinutes(1), env2);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddMinutes(1), env3);

        theScheduledJobs.Count().ShouldBe(3);

        theScheduledJobs.PlayAll();

        theScheduledJobs.Count().ShouldBe(0);
        sent.Count.ShouldBe(3);
        sent.ShouldContain(env1);
        sent.ShouldContain(env2);
        sent.ShouldContain(env3);
    }

    [Fact]
    public async Task empty_all()
    {
        var env1 = ObjectMother.Envelope();
        var env2 = ObjectMother.Envelope();
        var env3 = ObjectMother.Envelope();

        theScheduledJobs.Enqueue(DateTime.UtcNow.AddSeconds(1), env1);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddSeconds(1), env2);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddSeconds(1), env3);

        theScheduledJobs.Count().ShouldBe(3);

        await theScheduledJobs.EmptyAllAsync();

        theScheduledJobs.Count().ShouldBe(0);


        await Task.Delay(2000.Milliseconds());

        sent.Any().ShouldBeFalse();
    }

    [Fact]
    public void play_at_certain_time()
    {
        var env1 = ObjectMother.Envelope();
        var env2 = ObjectMother.Envelope();
        var env3 = ObjectMother.Envelope();

        theScheduledJobs.Enqueue(DateTime.UtcNow.AddHours(1), env1);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddHours(2), env2);
        theScheduledJobs.Enqueue(DateTime.UtcNow.AddHours(3), env3);

        theScheduledJobs.Play(DateTime.UtcNow.AddMinutes(150));

        sent.Count.ShouldBe(2);
        sent.ShouldContain(env1);
        sent.ShouldContain(env2);
        sent.ShouldNotContain(env3);

        theScheduledJobs.Count().ShouldBe(1);
    }
}