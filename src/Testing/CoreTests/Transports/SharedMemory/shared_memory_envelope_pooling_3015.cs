using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace CoreTests.Transports.SharedMemory;

public class shared_memory_envelope_pooling_3015
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task shared_memory_handoff_copies_pooled_envelope_before_sender_recycles_original()
    {
        var topicName = $"pooling-3015-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Serverless;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic(topicName);
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();
        var destination = new Uri($"shared-memory://{topicName}");
        var configuredAgent = runtime.Endpoints.GetOrBuildSendingAgent(destination);
        var sharedMemorySender = configuredAgent.Endpoint.ShouldBeOfType<SharedMemoryTopic>();

        var receivers = new[] { new BlockingReceiver(), new BlockingReceiver() };
        var subscriptions = receivers.Select((receiver, index) => new SharedMemorySubscription(
            sharedMemorySender,
            $"receiver-{index + 1}-{Guid.NewGuid():N}",
            EndpointRole.Application)).ToArray();
        for (var i = 0; i < subscriptions.Length; i++)
        {
            await subscriptions[i].BuildListenerAsync(runtime, receivers[i]);
        }

        var gatedSender = new GateSenderReturn(sharedMemorySender);
        var diagnosticAgent = new BufferedSendingAgent(
            runtime.LoggerFactory.CreateLogger<BufferedSendingAgent>(),
            runtime.MessageTracking,
            gatedSender,
            runtime.DurabilitySettings,
            sharedMemorySender,
            runtime,
            null);

        Task? sendTask = null;

        try
        {
            var envelope = runtime.AcquireOutgoingEnvelope(diagnosticAgent);
            var message = new PoolingProbe("original payload");
            envelope.Message = message;
            envelope.Destination = destination;
            envelope.Sender = diagnosticAgent;
            envelope.SendAttempts = 3;
            envelope.WasPersistedInOutbox = true;
            envelope.ResponseType = typeof(string);
            envelope.Response = "sender-side response";
            envelope.DoNotCascadeResponse = true;
            envelope.AlwaysPublishResponse = true;
            envelope.Batch = [new Envelope(new PoolingProbe("batch member"))];
            envelope.InBatch = true;
            envelope.HasBeenAcked = true;
            envelope.Failure = new InvalidOperationException("sender-side failure state");
            envelope.SetMetricsTag("org.unit", "platform");
            var originalId = envelope.Id;

            assertValid(envelope, true, "before Shared Memory enqueue");

            sendTask = envelope.StoreAndForwardAsync().AsTask();

            var senderEnvelope = await waitFor(
                gatedSender.SharedMemoryEnqueued,
                "SharedMemoryTopic.SendAsync to enqueue the envelope");
            var receiverEnvelopes = new[]
            {
                await waitFor(receivers[0].Entered, "the first Shared Memory receiver to retain its envelope"),
                await waitFor(receivers[1].Entered, "the second Shared Memory receiver to retain its envelope")
            };

            senderEnvelope.ShouldBeSameAs(envelope,
                "the sender must enqueue the original pooled Envelope instance");
            foreach (var receiverEnvelope in receiverEnvelopes)
            {
                receiverEnvelope.ShouldNotBeSameAs(senderEnvelope,
                    "Shared Memory must hand each subscription a new Envelope instance");
                receiverEnvelope.Message.ShouldBeSameAs(message);
                receiverEnvelope.Id.ShouldBe(originalId);
                assertValid(receiverEnvelope, false, "inside a blocked receiver before sender return");
                assertNoSenderLifecycle(receiverEnvelope);
                assertCustomMetricTagWasCopied(receiverEnvelope);
            }

            receiverEnvelopes[1].ShouldNotBeSameAs(receiverEnvelopes[0],
                "each Shared Memory subscription must own a distinct Envelope instance");

            gatedSender.AllowReturn();
            await waitFor(sendTask, "the sender to return the Envelope to the pool");

            assertReset(senderEnvelope, "after sender completion");
            foreach (var receiver in receivers)
            {
                receiver.IsBlocked.ShouldBeTrue(
                    "each receiver must remain blocked while its copy is inspected after sender recycle");
            }

            foreach (var receiverEnvelope in receiverEnvelopes)
            {
                assertValid(receiverEnvelope, false, "inside a blocked receiver after sender recycle");
                assertNoSenderLifecycle(receiverEnvelope);
                assertCustomMetricTagWasCopied(receiverEnvelope);
                receiverEnvelope.Id.ShouldBe(originalId);
            }
        }
        finally
        {
            gatedSender.AllowReturn();
            foreach (var receiver in receivers) receiver.AllowContinuation();

            if (sendTask != null)
            {
                await waitFor(sendTask, "the diagnostic sender cleanup");
            }

            for (var i = 0; i < receivers.Length; i++)
            {
                if (receivers[i].HasEntered)
                {
                    await waitFor(receivers[i].Completed, $"diagnostic receiver {i + 1} cleanup");
                }
            }

            await diagnosticAgent.DisposeAsync();
            foreach (var subscription in subscriptions) await subscription.StopAsync();
            await host.StopAsync();
            await SharedMemoryQueueManager.ClearAllAsync();
        }
    }

    private static void assertValid(Envelope envelope, bool expectedFromPool, string stage)
    {
        envelope.Id.ShouldNotBe(Guid.Empty, describe(envelope, stage));
        envelope.Message.ShouldBeOfType<PoolingProbe>(describe(envelope, stage));
        envelope.Destination.ShouldNotBeNull(describe(envelope, stage));
        envelope.MessageType.ShouldNotBeNullOrWhiteSpace(describe(envelope, stage));
        envelope.FromPool.ShouldBe(expectedFromPool, describe(envelope, stage));
    }

    private static void assertReset(Envelope envelope, string stage)
    {
        envelope.Id.ShouldBe(Guid.Empty, describe(envelope, stage));
        envelope.Message.ShouldBeNull(describe(envelope, stage));
        envelope.Destination.ShouldBeNull(describe(envelope, stage));
        envelope.FromPool.ShouldBeFalse(describe(envelope, stage));
    }

    private static void assertNoSenderLifecycle(Envelope envelope)
    {
        envelope.Sender.ShouldBeNull();
        envelope.Listener.ShouldBeNull();
        envelope.SendAttempts.ShouldBe(0);
        envelope.WasPersistedInOutbox.ShouldBeFalse();
        envelope.ResponseType.ShouldBeNull();
        envelope.Response.ShouldBeNull();
        envelope.DoNotCascadeResponse.ShouldBeFalse();
        envelope.AlwaysPublishResponse.ShouldBeFalse();
        envelope.Batch.ShouldBeNull();
        envelope.InBatch.ShouldBeFalse();
        envelope.HasBeenAcked.ShouldBeFalse();
        envelope.Failure.ShouldBeNull();
    }

    private static void assertCustomMetricTagWasCopied(Envelope envelope)
    {
        envelope.ToMetricsHeaders().ShouldContain(x =>
            x.Key == "org.unit" && x.Value != null && x.Value.Equals("platform"));
    }

    private static string describe(Envelope envelope, string stage)
    {
        return $"Envelope state {stage}: Id={envelope.Id}, " +
               $"Message={envelope.Message?.GetType().FullName ?? "null"}, " +
               $"Destination={envelope.Destination?.ToString() ?? "null"}, FromPool={envelope.FromPool}";
    }

    private static async Task<T> waitFor<T>(Task<T> task, string operation)
    {
        try
        {
            return await task.WaitAsync(Timeout);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(
                $"Timed out after {Timeout} waiting for {operation}.", e);
        }
    }

    private static async Task waitFor(Task task, string operation)
    {
        try
        {
            await task.WaitAsync(Timeout);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(
                $"Timed out after {Timeout} waiting for {operation}.", e);
        }
    }

    private sealed record PoolingProbe(string Value);

    private sealed class GateSenderReturn(ISender inner) : ISender
    {
        private readonly TaskCompletionSource _allowReturn =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<Envelope> _sharedMemoryEnqueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Envelope> SharedMemoryEnqueued => _sharedMemoryEnqueued.Task;

        public bool SupportsNativeScheduledSend => inner.SupportsNativeScheduledSend;

        public Uri Destination => inner.Destination;

        public Task<bool> PingAsync() => inner.PingAsync();

        public async ValueTask SendAsync(Envelope envelope)
        {
            await inner.SendAsync(envelope);

            _sharedMemoryEnqueued.TrySetResult(envelope);

            await _allowReturn.Task;
        }

        public void AllowReturn()
        {
            _allowReturn.TrySetResult();
        }
    }

    private sealed class BlockingReceiver : IReceiver
    {
        private readonly TaskCompletionSource _allowContinuation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<Envelope> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Envelope> Entered => _entered.Task;

        public Task Completed => _completed.Task;

        public bool HasEntered => _entered.Task.IsCompleted;

        public bool IsBlocked => HasEntered && !_completed.Task.IsCompleted;

        public IHandlerPipeline Pipeline => null!;

        public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
        {
            messages.Length.ShouldBe(1);
            return ReceivedAsync(listener, messages[0]);
        }

        public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
        {
            _entered.TrySetResult(envelope);

            await _allowContinuation.Task;

            _completed.TrySetResult();
        }

        public ValueTask DrainAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            AllowContinuation();
        }

        public void AllowContinuation()
        {
            _allowContinuation.TrySetResult();
        }
    }
}
