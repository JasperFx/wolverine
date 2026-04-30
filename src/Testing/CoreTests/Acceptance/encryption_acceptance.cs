using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance;

public class encryption_acceptance
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    public sealed record EncryptedPayload(string Secret);

    public static class EncryptedPayloadHandler
    {
        public static List<EncryptedPayload> Received = new();
        public static void Handle(EncryptedPayload payload) => Received.Add(payload);
    }

    public sealed record EncryptedNoOp(string Value);

    public static class EncryptedNoOpHandler
    {
        public static void Handle(EncryptedNoOp _) { /* no-op; failure paths are tested */ }
    }

    [Fact]
    public async Task encrypted_message_round_trips_end_to_end()
    {
        EncryptedPayloadHandler.Received.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));

                opts.PublishAllMessages().ToLocalQueue("encrypted-queue");
                opts.LocalQueue("encrypted-queue");
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        await host.TrackActivity().ExecuteAndWaitAsync(_ =>
            bus.PublishAsync(new EncryptedPayload("super-secret")));

        EncryptedPayloadHandler.Received.Single().Secret.ShouldBe("super-secret");
    }

    [Fact]
    public async Task envelope_on_the_wire_uses_encrypted_content_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.PublishAllMessages().ToLocalQueue("encrypted-queue");
                opts.LocalQueue("encrypted-queue");
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var session = await host.TrackActivity().ExecuteAndWaitAsync(_ =>
            bus.PublishAsync(new EncryptedPayload("x")));

        // Local queues do not serialize on send (in-memory pass-through), so
        // EncryptingMessageSerializer.WriteAsync is not invoked and the per-envelope
        // KeyIdHeader is not stamped here. We assert the routing-time content-type and
        // serializer selection — the actual byte-level encryption is covered by
        // EncryptingMessageSerializerTests in Commit Group C2.
        var sentEnvelope = session.Sent.SingleEnvelope<EncryptedPayload>();
        sentEnvelope.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        sentEnvelope.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();
    }

    [Fact]
    public async Task receive_with_unknown_key_id_routes_to_error_queue_two_host()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        // Sender host: knows "ghost" key, encrypts under it.
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "ghost",
                    new Dictionary<string, byte[]> { ["ghost"] = Key32(0x33) }));
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        // Receiver host: only knows "k1", will reject "ghost" with EncryptionKeyNotFoundException.
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.OnException<EncryptionKeyNotFoundException>().MoveToErrorQueue();
                opts.ListenAtPort(receiverPort);
                opts.ServiceName = "receiver";
            })
            .StartAsync();

        var session = await receiver
            .TrackActivity(TimeSpan.FromSeconds(10))
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForAnyDeadLetteredEnvelope())
            .ExecuteAndWaitAsync(_ =>
                sender.Services.GetRequiredService<IMessageBus>().PublishAsync(new EncryptedNoOp("x")));

        // Receive-side deserialization fails before the message body is materialized,
        // so envelope.Message is null. Wolverine's tracking pipeline does NOT propagate
        // the exception into the MovedToErrorQueue record's Exception slot, but it
        // does record a sibling "MessageFailed" event (stored under MessageEventType.Sent
        // in the session's record stream). Locate the failure record by its non-null
        // Exception and assert the captured exception type.
        session.MovedToErrorQueue.RecordsInOrder().ShouldNotBeEmpty();
        var failureRecord = session.AllRecordsInOrder()
            .Single(r => r.Exception is EncryptionKeyNotFoundException);
        failureRecord.Exception.ShouldBeOfType<EncryptionKeyNotFoundException>();
    }

    [Fact]
    public async Task receive_with_wrong_key_bytes_routes_to_error_queue()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        // Both hosts know key-id "k1" but with different bytes. AES-GCM auth tag
        // fails on receive => MessageDecryptionException => user policy moves to DLQ.
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x33) }));
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) })); // different bytes!
                opts.OnException<MessageDecryptionException>().MoveToErrorQueue();
                opts.ListenAtPort(receiverPort);
                opts.ServiceName = "receiver";
            })
            .StartAsync();

        var session = await receiver
            .TrackActivity(TimeSpan.FromSeconds(10))
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForAnyDeadLetteredEnvelope())
            .ExecuteAndWaitAsync(_ =>
                sender.Services.GetRequiredService<IMessageBus>().PublishAsync(new EncryptedNoOp("x")));

        // Same approach as the unknown-key test above.
        session.MovedToErrorQueue.RecordsInOrder().ShouldNotBeEmpty();
        var failureRecord = session.AllRecordsInOrder()
            .Single(r => r.Exception is MessageDecryptionException);
        failureRecord.Exception.ShouldBeOfType<MessageDecryptionException>();
    }
}

/// <summary>
/// Waits for ANY envelope to be dead-lettered. Used when the receive-side deserialization
/// fails before envelope.Message can be materialized, so the typed
/// WaitForDeadLetteredMessage&lt;T&gt; condition never matches.
/// </summary>
internal sealed class WaitForAnyDeadLetteredEnvelope : ITrackedCondition
{
    private bool _found;

    public void Record(EnvelopeRecord record)
    {
        if (record.MessageEventType == Wolverine.Tracking.MessageEventType.MovedToErrorQueue)
        {
            _found = true;
        }
    }

    public bool IsCompleted() => _found;
}
