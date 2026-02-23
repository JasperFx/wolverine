using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class InlineAzureServiceBusSenderTests
{
    [Fact]
    public void supports_native_scheduled_send()
    {
        var sender = createSender();
        sender.SupportsNativeScheduledSend.ShouldBeTrue();
    }

    [Fact]
    public void supports_native_scheduled_cancellation()
    {
        var sender = createSender();
        sender.SupportsNativeScheduledCancellation.ShouldBeTrue();
    }

    [Fact]
    public void implements_ISenderWithScheduledCancellation()
    {
        var sender = createSender();
        (sender is ISenderWithScheduledCancellation).ShouldBeTrue();
    }

    [Fact]
    public async Task cancel_with_wrong_type_throws_ArgumentException()
    {
        var sender = createSender();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await sender.CancelScheduledMessageAsync(Guid.NewGuid());
        });

        ex.Message.ShouldContain("Expected scheduling token of type long");
        ex.Message.ShouldContain("Guid");
    }

    [Fact]
    public async Task cancel_with_null_throws_ArgumentException()
    {
        var sender = createSender();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await sender.CancelScheduledMessageAsync(null!);
        });

        ex.Message.ShouldContain("Expected scheduling token of type long");
        ex.Message.ShouldContain("null");
    }

    private static InlineAzureServiceBusSender createSender()
    {
        var transport = new AzureServiceBusTransport();
        var queue = new AzureServiceBusQueue(transport, "test-queue");
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var serviceBusSender = Substitute.For<ServiceBusSender>();
        var logger = NullLogger.Instance;

        return new InlineAzureServiceBusSender(queue, mapper, serviceBusSender, logger, CancellationToken.None);
    }
}
