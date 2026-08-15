using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_189_fails_if_there_are_many_messages_in_queue_on_startup
{
    [Fact]
    public async Task be_able_to_start_up_with_large_number_of_messages_waiting_on_you()
    {
        var queueName = RabbitTesting.NextQueueName();
        var sender =


        await Host.CreateDefaultBuilder()

            #region sample_usage_of_send_inline
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts
                    .PublishAllMessages()
                    .ToRabbitQueue(queueName)

                    // This option is important inside of Serverless functions
                    .SendInline();
            })

            #endregion


            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var bus = sender.MessageBus();

        for (int i = 0; i < 1000; i++)
        {
            await bus.PublishAsync(new Bug189(Guid.NewGuid()));
        }

        await sender.StopAsync(TestContext.Current.CancellationToken);

        var waiter = Bug189Handler.WaitForCompletion(500, 120000);

        // Fire-and-forget the host startup so it doesn't block while processing
        // queued messages inline during startup
        var receiverTask = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();

                // TODO -- take in the parallel listener count within ProcessInline()? Just sugar, but still?
                opts.ListenToRabbitQueue(queueName).ProcessInline().ListenerCount(5);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // The receiver host is deliberately not awaited before the waiter -- StartAsync can block
            // draining a full queue under ProcessInline. But leaving it entirely unobserved meant a
            // host that failed to *start* produced no error at all: the waiter simply ran out its 120
            // seconds and the test reported a bare "System.TimeoutException : The operation has timed
            // out." with the real cause discarded. Race the two so a startup failure is rethrown as
            // itself.
            var finished = await Task.WhenAny(waiter, receiverTask);
            if (ReferenceEquals(finished, receiverTask) && receiverTask.IsFaulted)
            {
                await receiverTask;
            }

            await waiter;
        }
        finally
        {
            if (receiverTask.IsCompletedSuccessfully)
            {
                var host = await receiverTask;
                await host.StopAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                // Never leave the startup task's exception unobserved, whichever way we exit
                _ = receiverTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }
        }
    }

    public record Bug189(Guid Id);

    public static class Bug189Handler
    {
        private static TaskCompletionSource<int> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static int _count;
        private static int _expected;

        public static Task WaitForCompletion(int count, int millisecondTimeout)
        {
            // Reset per invocation. These are statics on a static class, so they survive for the life
            // of the worker process -- and the supervisor retries a failed test inside that same
            // process. A stale, already-completed _source made attempt 2 return a finished task and
            // "pass" without receiving anything, which is how this test has been showing up as
            // "passed on attempt 2" on green main runs while never actually re-verifying the fix.
            Interlocked.Exchange(ref _count, 0);
            _expected = count;
            _source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var source = _source;

            return source.Task.TimeoutAfterAsync(millisecondTimeout).ContinueWith(t =>
            {
                // A bare TimeoutException says nothing about how far the run got, which is the one
                // number separating "nothing was ever consumed" from "consumption was merely slow".
                if (t.IsFaulted && t.Exception!.InnerException is TimeoutException)
                {
                    throw new TimeoutException(
                        $"Only {Volatile.Read(ref _count)} of the expected {count} messages were handled within {millisecondTimeout}ms");
                }

                return t.GetAwaiter().GetResult();
            }, TaskScheduler.Default);
        }

        public static void Handle(Bug189 bug, Envelope envelope)
        {
            // The delivery-tag poisoning that used to live here has moved to
            // stale_delivery_tag_settling, which exercises the same unknown-tag path at a message
            // count low enough not to take the connection down with it. See GH-3950: every bad ack
            // makes the broker close that channel, and closing a channel with deliveries still in
            // flight races RabbitMQ.Client's receive loop into a library-initiated connection close
            // (code=541) that Wolverine cannot catch. At a thousand messages that fired on 4 of 5
            // local runs from a single poisoned tag, which is what made this test a chronic CI
            // failure. This test is about starting up against a full queue — that is the regression
            // it was written for, and it does not need chaos to prove it.

            // Five inline listeners run this concurrently. `_count++` on a volatile int is a
            // read-modify-write, so increments were being lost outright, and two threads could both
            // clear the threshold and call SetResult -- the second throwing InvalidOperationException
            // from inside a message handler.
            var count = Interlocked.Increment(ref _count);

            if (count >= _expected)
            {
                _source.TrySetResult(count);
            }
        }
    }
}