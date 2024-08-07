using JasperFx.Core;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Scheduled;

public class InMemoryScheduledJobProcessor : IScheduledJobProcessor
{
    private readonly Cache<Guid, InMemoryScheduledJob> _outstandingJobs = new();

    private readonly ILocalQueue _queue;

    public InMemoryScheduledJobProcessor(ILocalQueue queue)
    {
        _queue = queue;
    }

    public void Enqueue(DateTimeOffset executionTime, Envelope envelope)
    {
        _outstandingJobs[envelope.Id] = new InMemoryScheduledJob(this, envelope, executionTime);
    }

    public void PlayAll()
    {
        var outstanding = _outstandingJobs.ToArray();
        foreach (var job in outstanding) job.Enqueue();
    }

    public void Play(DateTime executionTime)
    {
        var outstanding = _outstandingJobs.Where(x => x.ExecutionTime <= executionTime).ToArray();
        foreach (var job in outstanding) job.Enqueue();
    }

    public Task EmptyAllAsync()
    {
        var outstanding = _outstandingJobs.ToArray();
        foreach (var job in outstanding) job.Cancel();

        return Task.CompletedTask;
    }

    public int Count()
    {
        return _outstandingJobs.Count;
    }

    ScheduledJob[] IScheduledJobProcessor.QueuedJobs()
    {
        return _outstandingJobs.ToArray().Select(x => x.ToReport()).ToArray();
    }

    public void Dispose()
    {
        var outstanding = _outstandingJobs.ToArray();
        foreach (var job in outstanding)
            job.Cancel();

        _outstandingJobs.ClearAll();
    }

    public class InMemoryScheduledJob : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly InMemoryScheduledJobProcessor _parent;
        private readonly Task _task;

        public InMemoryScheduledJob(InMemoryScheduledJobProcessor parent, Envelope envelope,
            DateTimeOffset executionTime)
        {
            _parent = parent;
            ExecutionTime = executionTime.ToUniversalTime();
            envelope.ScheduledTime = null;

            Envelope = envelope;

            _cancellation = new CancellationTokenSource();
            var delayTime = ExecutionTime.Subtract(DateTimeOffset.Now);
            _task = Task.Delay(delayTime, _cancellation.Token).ContinueWith(_ => publish(), TaskScheduler.Default);

            ReceivedAt = DateTimeOffset.Now;
        }

        public DateTimeOffset ExecutionTime { get; }

        public DateTimeOffset ReceivedAt { get; }

        public Envelope Envelope { get; }

        public void Dispose()
        {
            _task.Dispose();
        }

        private void publish()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                Enqueue();
            }
        }

        public void Cancel()
        {
            _cancellation.Cancel();
            _parent._outstandingJobs.Remove(Envelope.Id);
        }

        public ScheduledJob ToReport()
        {
            return new ScheduledJob(Envelope.Id)
            {
                ExecutionTime = ExecutionTime,
                ReceivedAt = ReceivedAt,
                MessageType = Envelope.MessageType
            };
        }

        public void Enqueue()
        {
            _parent._queue.Enqueue(Envelope);
            Cancel();
        }
    }
}