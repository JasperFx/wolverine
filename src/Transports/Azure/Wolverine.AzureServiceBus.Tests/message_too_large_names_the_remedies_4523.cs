using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-4523. AzureServiceBusSenderProtocol threw the core MessageTooLargeException, whose message gives the
/// limit and stops -- a number and no move to make. SQS's equivalent already named both of its remedies.
/// </summary>
public class message_too_large_names_the_remedies_4523
{
    private static Envelope AnEnvelope() => new()
    {
        Id = Guid.NewGuid(),
        MessageType = "BigPayload"
    };

    [Fact]
    public void the_azure_remedy_names_the_tier_the_claim_check_and_that_it_is_terminal()
    {
        var remedy = AzureServiceBusTransport.MessageTooLargeRemedy(new Uri("asb://queue/orders"));

        // the namespace tier that sets the ceiling
        remedy.ShouldContain("256 KB on Standard");
        remedy.ShouldContain("1 MB on Premium");
        remedy.ShouldContain("asb://queue/orders");

        // the way around the ceiling
        remedy.ShouldContain("claim check");
        remedy.ShouldContain("WolverineFx.AzureBlobStorage");

        // and that a retry cannot help, which SQS is explicit about and Azure was not
        remedy.ShouldContain("No retry can help");
        remedy.ShouldContain("sending failure policy");
    }

    [Fact]
    public void the_remedy_is_folded_into_the_exception_message()
    {
        var envelope = AnEnvelope();
        var remedy = AzureServiceBusTransport.MessageTooLargeRemedy(new Uri("asb://queue/orders"));

        var ex = new MessageTooLargeException(envelope, 262144, remedy);

        // the original message is preserved, so anything matching on it still works...
        ex.Message.ShouldContain($"Message {envelope.Id} of type 'BigPayload' is too large to fit in a batch");
        ex.Message.ShouldContain("262144");

        // ...with the remedy appended
        ex.Message.ShouldContain("claim check");
        ex.Remedy.ShouldBe(remedy);

        ex.EnvelopeId.ShouldBe(envelope.Id);
        ex.MessageType.ShouldBe("BigPayload");
        ex.MaxSizeInBytes.ShouldBe(262144);
    }

    [Fact]
    public void the_original_two_argument_constructor_is_unchanged()
    {
        // Sending failure policies key on this type, so the existing shape has to keep working exactly.
        var envelope = AnEnvelope();

        var ex = new MessageTooLargeException(envelope, 1024);

        ex.Message.ShouldBe(
            $"Message {envelope.Id} of type 'BigPayload' is too large to fit in a batch (max size: 1024 bytes)");
        ex.Remedy.ShouldBeNull();
    }
}
