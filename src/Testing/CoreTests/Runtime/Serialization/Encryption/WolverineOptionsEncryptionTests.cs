using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class WolverineOptionsEncryptionTests
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static IKeyProvider NewProvider() =>
        new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x01) });

    [Fact]
    public async Task use_encryption_swaps_default_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseEncryption(NewProvider()))
            .StartAsync();

        var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
        options.DefaultSerializer.ShouldBeOfType<EncryptingMessageSerializer>();
        options.DefaultSerializer.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
    }

    [Fact]
    public async Task use_encryption_keeps_inner_json_serializer_resolvable()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseEncryption(NewProvider()))
            .StartAsync();

        var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
        var json = options.TryFindSerializer(EnvelopeConstants.JsonContentType);
        json.ShouldNotBeNull();
        json.ShouldBeAssignableTo<IMessageSerializer>();
        json.ShouldNotBeAssignableTo<EncryptingMessageSerializer>();
    }

    [Fact]
    public void use_encryption_rejects_null_provider()
    {
        var opts = new WolverineOptions();
        Should.Throw<ArgumentNullException>(() => opts.UseEncryption(null!));
    }

    [Fact]
    public async Task per_type_encrypt_routes_only_matching_type_to_encrypting_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());
                opts.Policies.ForMessagesOfType<EncryptedTypeA>().Encrypt();

                opts.PublishAllMessages().ToLocalQueue("target");
                opts.LocalQueue("target");
            })
            .StartAsync();

        var bus = host.MessageBus();

        var sessionA = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new EncryptedTypeA("x")));
        var sessionB = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new PlainTypeB("x")));

        var sentEncrypted = sessionA.Sent.SingleEnvelope<EncryptedTypeA>();
        sentEncrypted.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        sentEncrypted.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();

        var sentPlain = sessionB.Sent.SingleEnvelope<PlainTypeB>();
        sentPlain.ContentType.ShouldBe(EnvelopeConstants.JsonContentType);
        sentPlain.Serializer.ShouldNotBeOfType<EncryptingMessageSerializer>();
    }

    [Fact]
    public async Task endpoint_encrypted_routes_outgoing_through_encrypting_content_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());

                opts.PublishAllMessages().ToLocalQueue("encrypted-q").Encrypted();
                opts.LocalQueue("encrypted-q");
            })
            .StartAsync();

        var bus = host.MessageBus();

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new EncryptedTypeA("x")));

        var sent = session.Sent.SingleEnvelope<EncryptedTypeA>();
        sent.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        sent.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();
    }

    [Fact]
    public void encrypt_without_registered_serializer_throws()
    {
        var opts = new WolverineOptions();
        opts.UseSystemTextJsonForSerialization();

        Should.Throw<InvalidOperationException>(() =>
            opts.Policies.ForMessagesOfType<EncryptedTypeA>().Encrypt());
    }

    [Fact]
    public void Encrypt_for_message_type_registers_in_RequiredEncryptedTypes()
    {
        var opts = new WolverineOptions();
        opts.UseEncryption(new InMemoryKeyProvider(
            "k1", new Dictionary<string, byte[]>
            {
                ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray()
            }));

        opts.Policies.ForMessagesOfType<SecretMessage>().Encrypt();

        opts.RequiredEncryptedTypes.ShouldContain(typeof(SecretMessage));
    }

    [Fact]
    public async Task RequiresEncryption_uses_listener_endpoint_uri_not_envelope_destination()
    {
        // Regression test: the listener-side guard must work even when the
        // inbound envelope has no Destination header (most broker transports
        // do not populate envelope.Destination on receive). The guard reads
        // the listener's own _endpoint.Uri, so this still fires.

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1", new Dictionary<string, byte[]>
                    {
                        ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray()
                    }));

                opts.LocalQueue("encryption-required-queue").RequireEncryption();
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = (LocalQueue?)runtime.Endpoints.EndpointByName("encryption-required-queue")
            ?? throw new InvalidOperationException("encryption-required-queue not found");

        // Sanity: the listener URI is in the required set after RequireEncryption().
        runtime.Options.RequiredEncryptedListenerUris.ShouldContain(endpoint.Uri);

        // Build an envelope as if it had arrived from a transport that does NOT
        // populate envelope.Destination on receive. Plain JSON content-type means
        // it has not been encrypted.
        var envelope = new Envelope
        {
            Destination = null,                                   // simulate broker transport
            ContentType = "application/json",                     // plain, not encrypted
            MessageType = typeof(EncryptionRequiredMsg).ToMessageTypeName(),
            Data        = System.Text.Encoding.UTF8.GetBytes("""{"Value":"forged"}""")
        };

        var receiver = (BufferedReceiver)endpoint.Agent!;
        var continuation = await receiver.Pipeline.TryDeserializeEnvelope(envelope);

        // The guard must fire via the listener's own endpoint URI. If it instead
        // keyed off envelope.Destination (which broker transports do not populate
        // on receive), the listener marker would be missed and the envelope would
        // fall through to deserialization rather than going to the dead-letter queue.
        var moveToErrorQueue = continuation.ShouldBeOfType<MoveToErrorQueue>();
        moveToErrorQueue.Exception.ShouldBeOfType<EncryptionPolicyViolationException>();
    }

    [Fact]
    public async Task RequireEncryption_on_listener_registers_listener_uri()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1", new Dictionary<string, byte[]>
                    {
                        ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray()
                    }));

                opts.LocalQueue("test-encrypted").RequireEncryption();
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var queueUri = runtime.Endpoints.EndpointByName("test-encrypted")?.Uri
            ?? throw new InvalidOperationException("test-encrypted queue not found");
        runtime.Options.RequiredEncryptedListenerUris.ShouldContain(queueUri);
    }

    [Fact]
    public void UseSystemTextJsonForSerialization_after_UseEncryption_keeps_encryption_active()
    {
        var opts = new WolverineOptions();
        var provider = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray() });
        opts.UseEncryption(provider);

        opts.UseSystemTextJsonForSerialization(_ => { });

        opts.DefaultSerializer.ShouldBeOfType<EncryptingMessageSerializer>();
    }

    [Fact]
    public async Task Encrypted_on_endpoint_without_UseEncryption_throws_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PublishAllMessages().ToLocalQueue("misconfigured").Encrypted();
                })
                .StartAsync();
        });

        ex.Message.ShouldContain("encrypting serializer");
    }

    [Fact]
    public async Task RequireEncryption_on_listener_without_UseEncryption_throws_at_startup()
    {
        // Symmetric to Encrypted() on the sender side: a listener marked
        // RequireEncryption() with no encrypting serializer registered would
        // dead-letter every inbound envelope, because there is no serializer
        // capable of producing the encrypted content-type for it to accept.
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.LocalQueue("misconfigured-listener").RequireEncryption();
                })
                .StartAsync();
        });

        ex.Message.ShouldContain("encrypting serializer");
    }

    [Fact]
    public void second_UseEncryption_call_throws_to_prevent_double_wrapping()
    {
        var opts = new WolverineOptions();
        var provider1 = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray() });
        var provider2 = new InMemoryKeyProvider("k2",
            new Dictionary<string, byte[]> { ["k2"] = Enumerable.Repeat((byte)0x33, 32).ToArray() });

        opts.UseEncryption(provider1);

        var ex = Should.Throw<InvalidOperationException>(() => opts.UseEncryption(provider2));
        ex.Message.ShouldContain("already been called");
    }

    [Fact]
    public async Task no_endpoint_pipeline_still_enforces_per_type_encryption_marker()
    {
        // The HandlerPipeline has two constructors — with and without endpoint.
        // The no-endpoint variant is used by non-listener invocation paths.
        // Its RequiresEncryption check must short-circuit the listener-URI
        // branch (no endpoint to read .Uri from) but still apply the per-type
        // marker so a plain envelope for a marked type is dead-lettered.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseEncryption(new InMemoryKeyProvider(
                    "k1", new Dictionary<string, byte[]>
                    {
                        ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray()
                    }));
                opts.Policies.ForMessagesOfType<EncryptionRequiredMsg>().Encrypt();
            })
            .StartAsync();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var pipelineNoEndpoint = new HandlerPipeline(runtime, runtime);

        var envelope = new Envelope
        {
            ContentType = "application/json",
            MessageType = typeof(EncryptionRequiredMsg).ToMessageTypeName(),
            Data        = System.Text.Encoding.UTF8.GetBytes("""{"Value":"forged"}""")
        };

        var continuation = await pipelineNoEndpoint.TryDeserializeEnvelope(envelope);

        var moveToErrorQueue = continuation.ShouldBeOfType<MoveToErrorQueue>();
        moveToErrorQueue.Exception.ShouldBeOfType<EncryptionPolicyViolationException>();
    }

    [Fact]
    public async Task unmarked_type_is_serialized_by_inner_not_encrypting_serializer()
    {
        // Negative AAD-binding test. With per-type Encrypt() registered for
        // EncryptedTypeA only, publishing PlainTypeB must NOT touch the
        // encrypting serializer at all — the routing layer picks the inner
        // (json) serializer, no key-id header is stamped, and no AAD binding
        // happens. Complements per_type_encrypt_routes_only_matching_type
        // by adding an explicit assertion that the encryption-only headers
        // are absent on the unmarked path.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());
                opts.Policies.ForMessagesOfType<EncryptedTypeA>().Encrypt();

                opts.PublishAllMessages().ToLocalQueue("target");
                opts.LocalQueue("target");
            })
            .StartAsync();

        var bus = host.MessageBus();

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(new PlainTypeB("x")));

        var sent = session.Sent.SingleEnvelope<PlainTypeB>();
        sent.Serializer.ShouldNotBeOfType<EncryptingMessageSerializer>();
        sent.ContentType.ShouldBe(EnvelopeConstants.JsonContentType);
        sent.Headers.ContainsKey(EncryptionHeaders.KeyIdHeader).ShouldBeFalse();
        sent.Headers.ContainsKey(EncryptionHeaders.InnerContentTypeHeader).ShouldBeFalse();
    }

    [Fact]
    public void second_RegisterEncryptionSerializer_call_throws_to_prevent_double_wrapping()
    {
        var opts = new WolverineOptions();
        opts.UseSystemTextJsonForSerialization();

        var provider1 = new InMemoryKeyProvider("k1",
            new Dictionary<string, byte[]> { ["k1"] = Enumerable.Repeat((byte)0x42, 32).ToArray() });
        var provider2 = new InMemoryKeyProvider("k2",
            new Dictionary<string, byte[]> { ["k2"] = Enumerable.Repeat((byte)0x33, 32).ToArray() });

        opts.RegisterEncryptionSerializer(provider1);

        var ex = Should.Throw<InvalidOperationException>(() => opts.RegisterEncryptionSerializer(provider2));
        ex.Message.ShouldContain("already registered");
    }
}

public sealed record EncryptedTypeA(string Value);
public sealed record PlainTypeB(string Value);
public sealed record SecretMessage(string S);
public sealed record EncryptionRequiredMsg(string Value);

public static class EncryptionRequiredMsgHandler
{
    public static void Handle(EncryptionRequiredMsg _) { }
}
