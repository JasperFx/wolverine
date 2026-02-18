using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Scheduled;

public class InMemoryScheduledJobProcessor : IScheduledJobProcessor
{
    private readonly Cache<Guid, InMemoryScheduledJob> _outstandingJobs = new();

    private readonly ILocalQueue _queue;
    private readonly ILogger _logger;

    public InMemoryScheduledJobProcessor(ILocalQueue queue, ILogger? logger = null)
    {
        _queue = queue;
        _logger = logger ?? NullLogger.Instance;
    }

    public void Enqueue(DateTimeOffset executionTime, Envelope envelope)
    {
        _logger.LogDebug("Enqueuing envelope {EnvelopeId} ({MessageType}) for in-memory scheduled execution at {ExecutionTime}", envelope.Id, envelope.MessageType, executionTime);
        _outstandingJobs[envelope.Id] = new InMemoryScheduledJob(this, envelope, executionTime);
    }

    public void PlayAll()
    {
        var outstanding = _outstandingJobs.ToArray();
        foreach (var job in outstanding) job.Enqueue();
    }

    public void Play(DateTime executionTime)
    {
        var outstanding = _outstandingJobs.ToArray();
        foreach (var job in outstanding)
        {
            if (job.ExecutionTime <= executionTime)
            {
                job.Enqueue();
            }
        }
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
        var jobs = _outstandingJobs.ToArray();
        var result = new ScheduledJob[jobs.Length];
        for (var i = 0; i < jobs.Length; i++)
        {
            result[i] = jobs[i].ToReport();
        }

        return result;
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
            var delayTime = ExecutionTime.Subtract(DateTimeOffset.UtcNow);
            if (delayTime <= TimeSpan.Zero)
            {
                _parent._logger.LogDebug("Scheduled envelope {EnvelopeId} ({MessageType}) firing immediately (execution time already passed)", envelope.Id, envelope.MessageType);
                _task = Task.Run(() => publish());
            }
            else
            {
                _parent._logger.LogDebug("Scheduled envelope {EnvelopeId} ({MessageType}) will fire after delay of {DelayTime}", envelope.Id, envelope.MessageType, delayTime);
                _task = Task.Delay(delayTime, _cancellation.Token).ContinueWith(_ => publish(), TaskScheduler.Default);
            }

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
            _parent._logger.LogDebug("In-memory scheduled job firing, enqueuing envelope {EnvelopeId} ({MessageType}) to local queue", Envelope.Id, Envelope.MessageType);
            _parent._queue.Enqueue(Envelope);
            Cancel();
        }
    }
}