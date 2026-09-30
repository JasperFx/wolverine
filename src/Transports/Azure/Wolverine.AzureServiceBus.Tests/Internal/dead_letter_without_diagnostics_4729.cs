using Azure.Messaging.ServiceBus;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// GH-4729. <c>buildDiagnosticProperties</c> found no GH-3474 diagnostic headers on an unstamped envelope and
/// returned null, and <c>DeadLetterAsync</c> forwarded that null as <c>propertiesToModify</c>. The real
/// <c>ServiceBusReceiver.DeadLetterMessageAsync</c> asserts that argument non-null, so the move threw
/// <c>ArgumentNullException</c> and the message never reached $DeadLetterQueue. A substitute receiver skips
/// that assertion, which is why the other dead letter tests never saw it -- so this pins the argument itself.
/// </summary>
public class dead_letter_without_diagnostics_4729
{
    [Fact]
    public async Task an_unstamped_envelope_never_passes_null_properties_to_the_sdk()
    {
        var receiver = Substitute.For<ServiceBusReceiver>();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(lockTokenGuid: Guid.NewGuid());
        var envelope = new AzureServiceBusEnvelope(message, receiver);

        await envelope.DeadLetterAsync(CancellationToken.None, "SomeException", "it blew up");

        var properties = receiver.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.DeadLetterMessageAsync))
            .GetArguments()[1];

        properties.ShouldNotBeNull(
            "Azure.Messaging.ServiceBus throws ArgumentNullException for a null propertiesToModify.");
    }
}
