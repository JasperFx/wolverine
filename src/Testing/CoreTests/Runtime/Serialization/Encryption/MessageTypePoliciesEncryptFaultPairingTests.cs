using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class MessageTypePoliciesEncryptFaultPairingTests
{
    public sealed record PaymentDetails(string CardNumber);

    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static IKeyProvider NewProvider() =>
        new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) });

    [Fact]
    public void Encrypt_T_marks_FaultT_as_required_encrypted()
    {
        var opts = new WolverineOptions();
        opts.UseSystemTextJsonForSerialization();
        opts.RegisterEncryptionSerializer(NewProvider());

        opts.Policies.ForMessagesOfType<PaymentDetails>().Encrypt();

        opts.RequiredEncryptedTypes.ShouldContain(typeof(PaymentDetails));
        opts.RequiredEncryptedTypes.ShouldContain(typeof(Fault<PaymentDetails>));
        opts.IsEncryptionRequired(typeof(Fault<PaymentDetails>)).ShouldBeTrue();
    }

    [Fact]
    public void Encrypt_T_skips_pairing_for_value_type_T()
    {
        var opts = new WolverineOptions();
        opts.UseSystemTextJsonForSerialization();
        opts.RegisterEncryptionSerializer(NewProvider());

        // Should not throw despite Fault<int> being an invalid closed generic
        // (Fault<T> requires T : class). The pairing path branches on IsValueType.
        opts.Policies.ForMessagesOfType<int>().Encrypt();

        // Value-type marker for T itself still registers (existing behavior).
        opts.RequiredEncryptedTypes.ShouldContain(typeof(int));

        // No Fault<int> entry — the closed generic would be invalid; the
        // FaultPublisher silently no-ops on value-type messages anyway.
        opts.RequiredEncryptedTypes
            .ShouldNotContain(t => t.IsGenericType
                && t.GetGenericTypeDefinition() == typeof(Fault<>)
                && t.GenericTypeArguments[0] == typeof(int));
    }

    [Fact]
    public async Task Manually_published_FaultT_routes_through_encrypting_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());
                opts.Policies.ForMessagesOfType<PaymentDetails>().Encrypt();

                opts.PublishAllMessages().ToLocalQueue("target");
                opts.LocalQueue("target");
            })
            .StartAsync();

        var bus = host.MessageBus();

        var manualFault = new Fault<PaymentDetails>(
            Message: new PaymentDetails("4111-1111-1111-1111"),
            Exception: ExceptionInfo.From(new InvalidOperationException("kaput")),
            Attempts: 3,
            FailedAt: DateTimeOffset.UtcNow,
            CorrelationId: null,
            ConversationId: Guid.NewGuid(),
            TenantId: null,
            Source: null,
            Headers: new Dictionary<string, string?>());

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => bus.PublishAsync(manualFault));

        var sent = session.Sent.SingleEnvelope<Fault<PaymentDetails>>();
        sent.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        sent.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();

        // Byte-level canary: materialize wire bytes and confirm the plaintext
        // card number is absent. Distinguishes "envelope was tagged for
        // encryption" from "the bytes that would reach the broker actually
        // are encrypted." Same canary the integration round-trip uses.
        var wireBytes = sent.Serializer!.Write(sent);
        System.Text.Encoding.UTF8.GetString(wireBytes)
            .ShouldNotContain("4111-1111-1111-1111");
    }
}
