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

public class in_memory_scheduled_messages
{
    private readonly InMemoryScheduledJobProcessor theScheduledJobs;
    private readonly InMemoryQueue queue;

    public in_memory_scheduled_messages()
    {
        queue = new InMemoryQueue();
        theScheduledJobs = new InMemoryScheduledJobProcessor(queue);
    }

    [Fact]
    public async Task run_multiple_messages_through()
    {
        var env1 = ObjectMother.Envelope();
        var env2 = ObjectMother.Envelope();
        var env3 = ObjectMother.Envelope();

        var waiter1 = queue.WaitForReceipt(env1);
        var waiter2 = queue.WaitForReceipt(env2);
        var waiter3 = queue.WaitForReceipt(env3);

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
        queue.Sent.ShouldBe([env1, env2, env3], ignoreOrder: true);
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

        queue.Sent.ShouldBeEmpty();

        await Task.Delay(2000.Milliseconds());

        queue.Sent.ShouldBeEmpty();
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

        queue.Sent.ShouldBe([env1, env2], ignoreOrder: true);

        theScheduledJobs.Count().ShouldBe(1);
    }
}

internal class InMemoryQueue : ILocalQueue
{
    private readonly Dictionary<Guid, TaskCompletionSource<Envelope>> _callbacks = [];
    public readonly IList<Envelope> Sent = [];

    public Uri Uri { get; } = null!;
    public Uri ReplyUri { get; } = null!;
    public Uri Destination { get; } = "local://delayed".ToUri();
    public Uri Alias { get; } = null!;

    public void Enqueue(Envelope envelope)
    {
        Sent.Add(envelope);
        if (_callbacks.TryGetValue(envelope.Id, out var value))
        {
            value.SetResult(envelope);
        }
    }

    public ValueTask EnqueueAsync(Envelope envelope)
    {
        Sent.Add(envelope);
        if (_callbacks.TryGetValue(envelope.Id, out var value))
        {
            value.SetResult(envelope);
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

    public IHandlerPipeline Pipeline => null!;

    public int QueueCount => 0;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public Task<Envelope> WaitForReceipt(Envelope envelope)
    {
        var source = new TaskCompletionSource<Envelope>();
        _callbacks.Add(envelope.Id, source);

        return source.Task;
    }
}
