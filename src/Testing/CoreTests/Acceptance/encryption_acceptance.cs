using System.Net;
using System.Net.Sockets;
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

        var bus = host.MessageBus();

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
                sender.MessageBus().PublishAsync(new EncryptedNoOp("x")));

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
                sender.MessageBus().PublishAsync(new EncryptedNoOp("x")));

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
                sender.MessageBus()
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
                sender.MessageBus()
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
        // Proves the listener-side encryption-required check does not block legitimate
        // encrypted traffic.
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
                sender.MessageBus()
                    .PublishAsync(new EncryptedPayload("legit-secret")));

        EncryptedPayloadHandler.Received.Single().Secret.ShouldBe("legit-secret");
    }

    [Fact]
    public async Task plain_message_for_unmarked_type_passes_when_encryption_is_configured()
    {
        // Rolling-deploy scenario. Receiver has UseEncryption configured, but
        // EncryptedNoOp is not marked as required and the listener is NOT
        // marked with .RequireEncryption() either. Sender publishes plain JSON.
        // Receiver MUST process it normally so unmarked types still flow during
        // gradual rollouts — the encryption guard only fires for marked types or
        // marked listeners. (Listener-marked endpoints DLQ unmarked types too;
        // that path is covered by receive_unencrypted_message_on_required_listener.)
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
                sender.MessageBus()
                    .PublishAsync(new EncryptedNoOp("rolling-deploy")));

        session.AllRecordsInOrder()
            .ShouldNotContain(r => r.MessageEventType == MessageEventType.MovedToErrorQueue);
    }

    [Fact]
    public async Task wire_does_not_contain_plaintext_when_encryption_is_required()
    {
        // In-process MITM proxy between sender and receiver: the Wolverine
        // sender publishes to snifferPort; the test's TcpListener accepts
        // that connection, dials the real receiver, and pumps both directions
        // while teeing the sender->receiver bytes into a MemoryStream. Any
        // plaintext fragment of the canary that ever crosses the wire shows
        // up in the captured buffer. This is the only test that proves the
        // bytes Wolverine actually transmits are not the plaintext — every
        // other encryption test inspects the serializer's output or relies
        // on a successful round-trip.
        var snifferPort  = PortFinder.GetAvailablePort();
        var receiverPort = PortFinder.GetAvailablePort();
        var canary       = "WIRE-CANARY-" + Guid.NewGuid().ToString("N");

        var captured = new MemoryStream();
        using var snifferCts = new CancellationTokenSource();
        var sniffer = new TcpListener(IPAddress.Loopback, snifferPort);
        sniffer.Start();

        var proxyTask = Task.Run(async () =>
        {
            try
            {
                using var inbound = await sniffer.AcceptTcpClientAsync(snifferCts.Token);
                using var inboundStream = inbound.GetStream();
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, receiverPort, snifferCts.Token);
                using var upstreamStream = upstream.GetStream();

                var senderToReceiver = PumpAsync(inboundStream, upstreamStream, captured, snifferCts.Token);
                var receiverToSender = PumpAsync(upstreamStream, inboundStream, sink: null, snifferCts.Token);

                await Task.WhenAny(senderToReceiver, receiverToSender);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException)    { }
            catch (IOException)                { }
            catch (SocketException)            { }
        });

        try
        {
            using var sender = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseEncryption(new InMemoryKeyProvider(
                        "k1",
                        new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));
                    opts.Policies.ForMessagesOfType<EncryptedPayload>().Encrypt();
                    opts.PublishAllMessages().To($"tcp://localhost:{snifferPort}");
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
                    sender.MessageBus()
                        .PublishAsync(new EncryptedPayload(canary)));

            EncryptedPayloadHandler.Received.ShouldContain(p => p.Secret == canary);

            byte[] capturedBytes;
            lock (captured) { capturedBytes = captured.ToArray(); }

            capturedBytes.Length.ShouldBeGreaterThan(0);
            // UTF8.GetString never throws on invalid sequences — substrings of
            // valid ASCII (the canary and the content-type marker) will match
            // contiguously regardless of the surrounding binary noise.
            var dump = System.Text.Encoding.UTF8.GetString(capturedBytes);
            dump.ShouldNotContain(canary);
            dump.ShouldContain(EncryptionHeaders.EncryptedContentType);
        }
        finally
        {
            snifferCts.Cancel();
            try { sniffer.Stop(); } catch { }
            try { await proxyTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task durable_persistence_path_serializes_ciphertext_not_plaintext()
    {
        // Disk-leak boundary. The outbox path is:
        //
        //   DestinationEndpoint.SendAsync   -> applies route.Rules (the
        //                                      encryption rule sets
        //                                      envelope.Serializer + ContentType)
        //   PersistOrSendAsync              -> hands envelope to outbox
        //   IMessageOutbox.StoreOutgoingAsync
        //                                   -> reads envelope.Data
        //   Envelope.Data getter            -> lazy; first read triggers
        //                                      Serializer.Write(this)
        //
        // So by the time the outbox writes bytes to disk, the encrypting
        // serializer has been assigned and the byte read is ciphertext.
        // This test locks the chain at the data-materialisation point: a
        // future change that pre-fills envelope.Data before the encryption
        // rule runs (or stores the inner serializer's output) would flip
        // this assertion red even without a real database.
        //
        // Both materialisation entry points are exercised:
        //   - sync Data getter (current outbox path via EnvelopeSerializer)
        //   - async GetDataAsync (the path a future migration would use,
        //     and the one EncryptingMessageSerializer's IAsyncMessageSerializer
        //     surface is built for).
        //
        // Out of scope: SendRawMessageAsync (DestinationEndpoint.cs) accepts
        // pre-serialized bytes that bypass the lazy serializer entirely. That
        // is by design — callers using it have already chosen their bytes —
        // and is not part of the contract this test locks.
        var canary = "PERSIST-CANARY-" + Guid.NewGuid().ToString("N");
        var encrypting = new EncryptingMessageSerializer(
            new Wolverine.Runtime.Serialization.SystemTextJsonSerializer(
                Wolverine.Runtime.Serialization.SystemTextJsonSerializer.DefaultOptions()),
            new InMemoryKeyProvider(
                "k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) }));

        // Sync path: mirrors what IMessageOutbox.StoreOutgoingAsync reads today.
        var syncEnvelope = new Envelope(new EncryptedPayload(canary))
        {
            Serializer  = encrypting,
            ContentType = encrypting.ContentType
        };
        var syncBytes = syncEnvelope.Data!;
        syncBytes.Length.ShouldBeGreaterThan(0);
        System.Text.Encoding.UTF8.GetString(syncBytes).ShouldNotContain(canary);
        syncEnvelope.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);

        // Async path: locks the same contract for any persistence-layer
        // migration that switches to GetDataAsync (preferred for async
        // serializers and a known refactor target).
        var asyncEnvelope = new Envelope(new EncryptedPayload(canary))
        {
            Serializer  = encrypting,
            ContentType = encrypting.ContentType
        };
        var asyncBytes = (await asyncEnvelope.GetDataAsync())!;
        asyncBytes.Length.ShouldBeGreaterThan(0);
        System.Text.Encoding.UTF8.GetString(asyncBytes).ShouldNotContain(canary);
        asyncEnvelope.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
    }

    private static async Task PumpAsync(NetworkStream src, NetworkStream dst, MemoryStream? sink, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) return;
                // Capture BEFORE forwarding: this guarantees that any byte the
                // receiver could possibly have observed is already in 'sink'
                // when the test asserts after WaitForMessageToBeReceivedAt.
                if (sink is not null) lock (sink) { sink.Write(buf, 0, n); }
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)    { }
        catch (IOException)                { }
        catch (SocketException)            { }
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
                sender.MessageBus()
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
