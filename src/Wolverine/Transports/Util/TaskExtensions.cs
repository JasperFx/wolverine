using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wolverine.Transports.Util;

public static class TaskExtensions
{
    public static Task TimeoutAfterAsync(this Task task, int millisecondsTimeout)
    {
#pragma warning disable VSTHRD105
        return task.ContinueWith(_ => true).TimeoutAfterAsync(millisecondsTimeout);
#pragma warning restore VSTHRD105
    }


    // All of this was taken from https://blogs.msdn.microsoft.com/pfxteam/2011/11/10/crafting-a-task-timeoutafter-method/
    public static Task<T> TimeoutAfterAsync<T>(this Task<T> task, int millisecondsTimeout)
    {
        // Short-circuit #1: infinite timeout or task already completed
        if (task.IsCompleted || millisecondsTimeout == Timeout.Infinite)
        {
#pragma warning disable VSTHRD003
            return task;
#pragma warning restore VSTHRD003
        }

        // tcs.Task will be returned as a proxy to the caller
        var tcs =
            new TaskCompletionSource<T>();

        // Short-circuit #2: zero timeout
        if (millisecondsTimeout == 0)
        {
            // We've already timed out.
            tcs.SetException(new TimeoutException());
            return tcs.Task;
        }

        // Set up a timer to complete after the specified timeout period
        var timer = new Timer(state =>
        {
            // Recover your state information
            var myTcs = (TaskCompletionSource<T>)state!;

            // Fault our proxy with a TimeoutException
            myTcs.TrySetException(new TimeoutException());
        }, tcs, millisecondsTimeout, Timeout.Infinite);

        // Wire up the logic for what happens when source task completes
#pragma warning disable VSTHRD110
        task.ContinueWith((antecedent, state) =>
#pragma warning restore VSTHRD110
            {
                // Recover our state data
                var tuple =
                    (Tuple<Timer, TaskCompletionSource<T>>)state!;

                // Cancel the Timer
                tuple.Item1.Dispose();

                // Marshal results to proxy
                MarshalTaskResults(antecedent, tuple.Item2);
            },
            Tuple.Create(timer, tcs),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return tcs.Task;
    }

    internal static void MarshalTaskResults<TResult>(
        Task source, TaskCompletionSource<TResult> proxy)
    {
        switch (source.Status)
        {
            case TaskStatus.Faulted:
                if (source.Exception != null)
                {
                    proxy.TrySetException(source.Exception);
                }

                break;
            case TaskStatus.Canceled:
                proxy.TrySetCanceled();
                break;
            case TaskStatus.RanToCompletion:
                var castedSource = source as Task<TResult>;
                proxy.TrySetResult(
                    (castedSource == null
                        ? default
                        : // source is a Task
#pragma warning disable VSTHRD002
                        castedSource.Result)!); // source is a Task<TResult>
#pragma warning restore VSTHRD002
                break;
        }
    }
}
