using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Util;
using Xunit;

namespace CoreTests.ErrorHandling.Faults.Integration;

/// <summary>
/// Pre-handler crypto failures must produce no auto-published <see cref="Fault{T}"/>
/// because the message instance is unavailable to wrap. These tests pin the
/// pipeline-level half of that guarantee: <see cref="HandlerPipeline.TryDeserializeEnvelope"/>
/// returns a <see cref="MoveToErrorQueue"/> with the expected crypto exception
/// type, and the resulting envelope has no <c>Message</c>. The transitive
/// "no fault is published" claim follows from
/// <see cref="Wolverine.ErrorHandling.FaultPublisher"/>'s null-message short-circuit
/// (covered by <c>FaultPublisherTests.no_op_when_envelope_message_is_null</c>).
/// </summary>
public class FaultCryptoExceptionGuardTests
{
    public record EncryptedThing(string Value);

    public class EncryptedThingHandler
    {
        public static Task Handle(EncryptedThing _) => Task.CompletedTask;
    }

    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static IKeyProvider KnowsK1Only() =>
        new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) });

    private static (HandlerPipeline pipeline, IHost host) BuildPipeline(
        Action<WolverineOptions> configure)
    {
        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishFaultEvents();
                configure(opts);
            })
            .Start();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var pipeline = new HandlerPipeline(runtime, runtime);
        return (pipeline, host);
    }

    [Fact]
    public async Task try_deserialize_returns_move_to_error_queue_for_message_decryption_failure()
    {
        var (pipeline, host) = BuildPipeline(opts => opts.UseEncryption(KnowsK1Only()));
        try
        {
            // 64 bytes is well above the 28-byte minimum (12-nonce + 16-tag),
            // so AEAD failure is the real code path exercised — not the early
            // "too short" guard at EncryptingMessageSerializer.cs:212.
            var garbled = new byte[64];
            new Random(42).NextBytes(garbled);

            var envelope = new Envelope
            {
                ContentType = EncryptionHeaders.EncryptedContentType,
                MessageType = typeof(EncryptedThing).ToMessageTypeName(),
                Data        = garbled,
            };
            envelope.Headers[EncryptionHeaders.KeyIdHeader] = "k1";
            envelope.Headers[EncryptionHeaders.InnerContentTypeHeader] = "application/json";

            var continuation = await pipeline.TryDeserializeEnvelope(envelope);

            var moveToErrorQueue = continuation.ShouldBeOfType<MoveToErrorQueue>();
            moveToErrorQueue.Exception.ShouldBeOfType<MessageDecryptionException>();
            // envelope.Message stays null; FaultPublisher will short-circuit
            // (see no_op_when_envelope_message_is_null in FaultPublisherTests).
            envelope.Message.ShouldBeNull();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task try_deserialize_returns_move_to_error_queue_for_unknown_encryption_key()
    {
        var (pipeline, host) = BuildPipeline(opts => opts.UseEncryption(KnowsK1Only()));
        try
        {
            // Body shape is irrelevant — key fetch happens before body inspection.
            // Properly-sized body is defense-in-depth against future reorderings
            // of EncryptingMessageSerializer.ReadFromDataAsync.
            var bytes = new byte[64];
            new Random(7).NextBytes(bytes);

            var envelope = new Envelope
            {
                ContentType = EncryptionHeaders.EncryptedContentType,
                MessageType = typeof(EncryptedThing).ToMessageTypeName(),
                Data        = bytes,
            };
            envelope.Headers[EncryptionHeaders.KeyIdHeader] = "k2"; // unknown to provider
            envelope.Headers[EncryptionHeaders.InnerContentTypeHeader] = "application/json";

            var continuation = await pipeline.TryDeserializeEnvelope(envelope);

            var moveToErrorQueue = continuation.ShouldBeOfType<MoveToErrorQueue>();
            moveToErrorQueue.Exception.ShouldBeOfType<EncryptionKeyNotFoundException>();
            envelope.Message.ShouldBeNull();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task try_deserialize_returns_move_to_error_queue_for_encryption_policy_violation()
    {
        var (pipeline, host) = BuildPipeline(opts =>
        {
            opts.UseSystemTextJsonForSerialization();
            // KnowsK1Only() satisfies RegisterEncryptionSerializer's requirement
            // for an IKeyProvider; the actual key value never matters here
            // because the receive-side guard rejects before any serializer runs.
            opts.RegisterEncryptionSerializer(KnowsK1Only());
            opts.Policies.ForMessagesOfType<EncryptedThing>().Encrypt();
        });
        try
        {
            // Plain-JSON envelope of a type marked Encrypt(). Receive-side
            // guard at HandlerPipeline.cs:118-121 rejects before any serializer.
            var envelope = new Envelope
            {
                ContentType = "application/json",
                MessageType = typeof(EncryptedThing).ToMessageTypeName(),
                Data        = System.Text.Encoding.UTF8.GetBytes("""{"Value":"forged"}"""),
            };

            var continuation = await pipeline.TryDeserializeEnvelope(envelope);

            var moveToErrorQueue = continuation.ShouldBeOfType<MoveToErrorQueue>();
            moveToErrorQueue.Exception.ShouldBeOfType<EncryptionPolicyViolationException>();
            envelope.Message.ShouldBeNull();
        }
        finally { await host.StopAsync(); }
    }
}
