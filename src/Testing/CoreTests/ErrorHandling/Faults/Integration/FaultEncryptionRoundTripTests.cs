using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling.Faults.Integration;

public class FaultEncryptionRoundTripTests
{
    public sealed record PaymentDetails(string CardNumber);

    public class AlwaysFailsHandler
    {
        public static Task Handle(PaymentDetails _) =>
            throw new InvalidOperationException("synthetic payment failure");
    }

    public class FaultCollector
    {
        public List<Fault<PaymentDetails>> Faults { get; } = new();
    }

    public class FaultCollectorHandler
    {
        public Task Handle(Fault<PaymentDetails> f, FaultCollector collector)
        {
            collector.Faults.Add(f);
            return Task.CompletedTask;
        }
    }

    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static IKeyProvider NewProvider() =>
        new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = Key32(0x42) });

    [Fact]
    public async Task auto_published_fault_for_encrypted_type_routes_through_encrypting_serializer()
    {
        var collector = new FaultCollector();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(collector);
                opts.OnException<Exception>().MoveToErrorQueue();

                opts.UseSystemTextJsonForSerialization();
                opts.RegisterEncryptionSerializer(NewProvider());
                opts.Policies.ForMessagesOfType<PaymentDetails>().Encrypt();

                opts.PublishFaultEvents();
            })
            .StartAsync();

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new PaymentDetails("4111-1111-1111-1111"));

        var faultEnvelope = session.AutoFaultsPublished
            .Envelopes()
            .Single(e => e.Message is Fault<PaymentDetails>);

        // Pairing took effect: the auto-fault envelope was routed through the
        // encrypting serializer, so any transport that materializes wire bytes
        // from envelope.Serializer.Write would produce ciphertext.
        faultEnvelope.ContentType.ShouldBe(EncryptionHeaders.EncryptedContentType);
        faultEnvelope.Serializer.ShouldBeOfType<EncryptingMessageSerializer>();

        // Materialize the wire bytes and assert plaintext does not appear in
        // them. This is the strongest direct evidence that ciphertext, not
        // the original card number, is what would reach a real broker.
        var wireBytes = faultEnvelope.Serializer!.Write(faultEnvelope);
        var asUtf8 = System.Text.Encoding.UTF8.GetString(wireBytes);
        asUtf8.ShouldNotContain("4111-1111-1111-1111");

        // Sanity: the in-process subscriber still received the fault (the local
        // queue path decrypts because the encrypting serializer is registered
        // for the encrypted content-type).
        collector.Faults.Single().Message.CardNumber.ShouldBe("4111-1111-1111-1111");
    }
}
