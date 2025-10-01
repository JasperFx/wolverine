using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;

namespace DocumentationSamples;

public class ExceptionHandling;

public static class AppWithErrorHandling
{
    public static async Task concurrency_retries()
    {
        #region sample_simple_retries_on_concurrency_exception

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts
                // On optimistic concurrency failures from Marten
                .OnException<ConcurrencyException>()
                .RetryWithCooldown(100.Milliseconds(), 250.Milliseconds(), 500.Milliseconds())
                .Then.MoveToErrorQueue();
        });

        #endregion
    }
    
    public static async Task sample()
    {
        #region sample_AppWithErrorHandling

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // On a SqlException, reschedule the message to be retried
                // at 3 seconds, then 15, then 30 seconds later
                opts.Policies.OnException<SqlException>()
                    .ScheduleRetry(3.Seconds(), 15.Seconds(), 30.Seconds());
            }).StartAsync();

        #endregion
    }

    public static async Task with_scripted_error_handling()
    {
        #region sample_AppWithScriptedErrorHandling

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.OnException<TimeoutException>()
                    // Just retry the message again on the
                    // first failure
                    .RetryOnce()

                    // On the 2nd failure, put the message back into the
                    // incoming queue to be retried later
                    .Then.Requeue()

                    // Or almost the same, but pause before requeue-ing
                    .Then.PauseThenRequeue(500.Milliseconds())

                    // On the 3rd failure, retry the message again after a configurable
                    // cool-off period. This schedules the message
                    .Then.ScheduleRetry(15.Seconds())

                    // On the 4th failure, move the message to the dead letter queue
                    .Then.MoveToErrorQueue()

                    // Or instead you could just discard the message and stop
                    // all processing too!
                    .Then.Discard().AndPauseProcessing(5.Minutes());

                // Obviously use this with caution, but this allows you
                // to tell Wolverine to requeue an exception on failures no
                // matter how many attempts have been made already
                opts.OnException<NotReadyException>()
                    .RequeueIndefinitely();
            }).StartAsync();

        #endregion
    }

    public static async Task with_scheduled_retry()
    {
        #region sample_using_scheduled_retry

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.OnException<TimeoutException>()
                    // Just retry the message again on the
                    // first failure
                    .RetryOnce()

                    // On the 2nd failure, put the message back into the
                    // incoming queue to be retried later
                    .Then.Requeue()

                    // On the 3rd failure, retry the message again after a configurable
                    // cool-off period. This schedules the message
                    .Then.ScheduleRetry(15.Seconds())

                    // On the next failure, move the message to the dead letter queue
                    .Then.MoveToErrorQueue();

            }).StartAsync();

        #endregion
    }
}

public class SqlException : Exception;

public class NotReadyException : Exception;