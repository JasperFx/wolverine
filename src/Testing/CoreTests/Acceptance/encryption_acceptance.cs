using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance;

public class encryption_acceptance : IDisposable
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    public encryption_acceptance()
    {
        // The handler-receive lists are static (Wolverine handlers are static
        // methods discovered by convention), so per-test isolation has to be
        // enforced explicitly. xUnit constructs a new test class instance per
        // [Fact], so clearing here gives every test a clean slate without
        // relying on each test to remember to .Clear() up front.
        EncryptedPayloadHandler.Received.Clear();
        SensitiveSubtypeHandler.Received.Clear();
    }

    public void Dispose()
    {
        EncryptedPayloadHandler.Received.Clear();
        SensitiveSubtypeHandler.Received.Clear();
    }

    public sealed record EncryptedPayload(string Secret);

    public static class EncryptedPayloadHandler
    {
        // ConcurrentBag protects against handler invocations from different
        // listener threads racing on List<T>.Add — a real hazard once xUnit
        // class-parallel runs ever shares a process with these tests.
        public static readonly System.Collections.Concurrent.ConcurrentBag<EncryptedPayload> Received = new();
        public static void Handle(EncryptedPayload payload) => Received.Add(payload);
    }

    public sealed record EncryptedNoOp(string Value);

    public static class EncryptedNoOpHandler
    {
        public static void Handle(EncryptedNoOp _) { /* no-op; failure paths are tested */ }
    }

    public interface ISensitivePayload { }

    public sealed record SensitiveSubtype(string Secret) : ISensitivePayload;

    public static class SensitiveSubtypeHandler
    {
        public static readonly System.Collections.Concurrent.ConcurrentBag<SensitiveSubtype> Received = new();
        public static void Handle(SensitiveSubtype payload) => Received.Add(payload);
    }

    [Fact]
    public async Task routing_assigns_encrypting_serializer_for_published_message()
    {
        // Local queues do not serialize on send (in-memory pass-through), so
        // EncryptingMessageSerializer.WriteAsync is NOT invoked here and no
        // per-envelope KeyIdHeader is stamped. This is a routing-decision test:
        // it verifies the published envelope is tagged with the encrypted
        // content-type and the encrypting serializer is selected. Byte-level
        // encryption (and the on-the-wire shape) is covered by
        // EncryptingMessageSerializerTests.
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

        session.ShouldHaveDeadLetteredWith<EncryptionKeyNotFoundException>();
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

        session.ShouldHaveDeadLetteredWith<MessageDecryptionException>();
    }

    [Fact]
    public async Task receive_unencrypted_message_for_required_type_routes_to_error_queue()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        // Sender does NOT call UseEncryption — emits plain JSON for EncryptedPayload.
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        // Receiver marks EncryptedPayload as encryption-required. The HandlerPipeline
        // guard must DLQ the forged plain-JSON envelope before any serializer runs.
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.Policies.ForMessagesOfType<EncryptedPayload>().Encrypt();
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
                sender.Services.GetRequiredService<IMessageBus>()
                    .PublishAsync(new EncryptedPayload("forged-plaintext")));

        session.ShouldHaveDeadLetteredWith<EncryptionPolicyViolationException>();
        EncryptedPayloadHandler.Received.ShouldNotContain(p => p.Secret == "forged-plaintext");
    }

    [Fact]
    public async Task receive_unencrypted_message_on_required_listener_routes_to_error_queue()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        // Sender does NOT call UseEncryption — emits plain JSON.
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        // Marker is on the LISTENER (.RequireEncryption()), not on the message type.
        // The HandlerPipeline guard must DLQ the forged plain-JSON envelope via the
        // destination-URI branch of RequiresEncryption, not via type resolution.
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.ListenAtPort(receiverPort).RequireEncryption();
                opts.ServiceName = "receiver";
            })
            .StartAsync();

        var session = await receiver
            .TrackActivity(TimeSpan.FromSeconds(10))
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForAnyDeadLetteredEnvelope())
            .ExecuteAndWaitAsync(_ =>
                sender.Services.GetRequiredService<IMessageBus>()
                    .PublishAsync(new EncryptedPayload("forged-plaintext-listener")));

        session.ShouldHaveDeadLetteredWith<EncryptionPolicyViolationException>();
        EncryptedPayloadHandler.Received.ShouldNotContain(p => p.Secret == "forged-plaintext-listener");
    }

    [Fact]
    public async Task encrypted_message_for_required_type_round_trips_two_host()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        // Negative control: both sides configured with UseEncryption + per-type marker.
        // Encrypted bytes go over the wire, decrypt successfully, and the handler runs.
        // Proves the C1 guard does not block legitimate encrypted traffic.
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.Policies.ForMessagesOfType<EncryptedPayload>().Encrypt();
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.Policies.ForMessagesOfType<EncryptedPayload>().Encrypt();
                opts.ListenAtPort(receiverPort);
                opts.ServiceName = "receiver";
            })
            .StartAsync();

        await receiver
            .TrackActivity(TimeSpan.FromSeconds(10))
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<EncryptedPayload>(receiver)
            .ExecuteAndWaitAsync(_ =>
                sender.Services.GetRequiredService<IMessageBus>()
                    .PublishAsync(new EncryptedPayload("legit-secret")));

        EncryptedPayloadHandler.Received.Single().Secret.ShouldBe("legit-secret");
    }

    [Fact]
    public async Task plain_message_for_unmarked_type_passes_when_encryption_is_configured()
    {
        // Rolling-deploy scenario. Receiver has UseEncryption configured, but
        // EncryptedNoOp is not marked as required. Sender publishes plain JSON.
        // Receiver MUST process it normally so unmarked types still flow during
        // gradual rollouts — the encryption guard only fires for marked types or
        // marked listeners.
        var receiverPort = PortFinder.GetAvailablePort();

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                // EncryptedNoOp deliberately NOT marked.
                opts.ListenAtPort(receiverPort);
                opts.ServiceName = "receiver";
            })
            .StartAsync();

        var session = await receiver
            .TrackActivity(TimeSpan.FromSeconds(10))
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<EncryptedNoOp>(receiver)
            .ExecuteAndWaitAsync(_ =>
                sender.Services.GetRequiredService<IMessageBus>()
                    .PublishAsync(new EncryptedNoOp("rolling-deploy")));

        session.AllRecordsInOrder()
            .ShouldNotContain(r => r.MessageEventType == MessageEventType.MovedToErrorQueue);
    }

    [Fact]
    public async Task receive_unencrypted_message_for_required_supertype_routes_to_error_queue()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        // Sender does NOT call UseEncryption — emits plain JSON for SensitiveSubtype.
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().To($"tcp://localhost:{receiverPort}");
                opts.ServiceName = "sender";
            })
            .StartAsync();

        // Receiver marks the SUPERTYPE (interface) as encryption-required. The
        // wire MessageType resolves to the concrete SensitiveSubtype, which is
        // not in RequiredEncryptedTypes by exact match. The polymorphic guard
        // must still DLQ the envelope before the serializer runs.
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1",
                    new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                opts.Policies.ForMessagesOfType<ISensitivePayload>().Encrypt();
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
                sender.Services.GetRequiredService<IMessageBus>()
                    .PublishAsync(new SensitiveSubtype("forged-plaintext-subtype")));

        session.ShouldHaveDeadLetteredWith<EncryptionPolicyViolationException>();
        SensitiveSubtypeHandler.Received
            .ShouldNotContain(p => p.Secret == "forged-plaintext-subtype");
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

internal static class TrackedSessionEncryptionAssertions
{
    /// <summary>
    /// Asserts that an envelope was dead-lettered AND that some tracking record
    /// in the session carries an exception of <typeparamref name="TException"/>.
    /// Tolerates whichever record slot the tracking pipeline writes the exception
    /// into — currently a sibling MessageFailed record, but historically and
    /// potentially again the MovedToErrorQueue record itself. Without this
    /// helper, a future change to where the exception is recorded would silently
    /// invalidate every receive-side test even though the production behavior
    /// (DLQ + correct exception) is unchanged.
    /// </summary>
    public static void ShouldHaveDeadLetteredWith<TException>(this Wolverine.Tracking.ITrackedSession session)
        where TException : Exception
    {
        session.MovedToErrorQueue.RecordsInOrder().ShouldNotBeEmpty();

        var allRecords = session.AllRecordsInOrder().ToList();
        var matched = allRecords.FirstOrDefault(r => r.Exception is TException);

        matched.ShouldNotBeNull(
            customMessage: $"Expected a tracking record carrying {typeof(TException).Name}. " +
                           $"Got record exceptions: " +
                           $"[{string.Join(", ", allRecords.Select(r => r.Exception?.GetType().Name ?? "<no-exception>"))}].");
    }
}
