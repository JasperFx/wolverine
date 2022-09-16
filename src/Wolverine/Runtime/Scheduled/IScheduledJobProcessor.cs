using System;
using System.Threading.Tasks;

namespace Wolverine.Runtime.Scheduled;

public interface IScheduledJobProcessor : IDisposable
{
    void Enqueue(DateTimeOffset executionTime, Envelope envelope);

    void PlayAll();

    void Play(DateTime executionTime);

    Task EmptyAllAsync();

    int Count();

    ScheduledJob[] QueuedJobs();
}
