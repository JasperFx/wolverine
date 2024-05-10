using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus;
using NServiceBusRabbitMqService;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace InteropTests.NServiceBus;

public class NServiceBusSpecs : IClassFixture<NServiceBusFixture>
{
    private readonly NServiceBusFixture theFixture;

    public NServiceBusSpecs(NServiceBusFixture fixture)
    {
        theFixture = fixture;
    }

    [Fact]
    public async Task nservicebus_sends_message_to_wolverine()
    {
        ResponseHandler.Received.Clear();

        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString();

        var session = await theFixture.Wolverine.ExecuteAndWaitAsync(async () =>
        {
            var options = new PublishOptions();
            options.SetMessageId(messageId);
            var sender = theFixture.NServiceBus.Services.GetRequiredService<IMessageSession>();
            await sender.Publish(new ResponseMessage { Id = id }, options);
        }, 60000);

        var envelope = ResponseHandler.Received.FirstOrDefault();
        envelope.Message.ShouldBeOfType<ResponseMessage>().Id.ShouldBe(id);
        envelope.ShouldNotBeNull();

        envelope.CorrelationId.ShouldBe(messageId);
        envelope.Id.ShouldNotBe(Guid.Empty);
        envelope.ConversationId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task nservicebus_sends_interface_to_wolverine_who_only_understands_concretes()
    {
        ResponseHandler.Received.Clear();

        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid().ToString();

        var session = await theFixture.Wolverine.ExecuteAndWaitAsync(async () =>
        {
            var options = new SendOptions();
            options.SetMessageId(messageId);
            options.SetDestination("wolverine");
            var sender = theFixture.NServiceBus.Services.GetRequiredService<IMessageSession>();
            await sender.Send<IInterfaceMessage>(x => x.Id = id, options);
        }, 60000);

        var envelope = ResponseHandler.Received.FirstOrDefault();
        envelope.Message.ShouldBeOfType<ConcreteMessage>().Id.ShouldBe(id);
        envelope.ShouldNotBeNull();

        envelope.CorrelationId.ShouldNotBeNull();
        envelope.CorrelationId.ShouldBe(messageId);
        envelope.Id.ShouldNotBe(Guid.Empty);
        envelope.ConversationId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task wolverine_sends_message_to_nservicebus_that_then_responds()
    {
        ResponseHandler.Received.Clear();

        var id = Guid.NewGuid();

        var session = await theFixture.Wolverine.TrackActivity().Timeout(10.Minutes())
            .WaitForMessageToBeReceivedAt<ResponseMessage>(theFixture.Wolverine)
            .SendMessageAndWaitAsync(new InitialMessage { Id = id });

        ResponseHandler.Received
            .Select(x => x.Message)
            .OfType<ResponseMessage>()
            .Any(x => x.Id == id)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task wolverine_sends_message_to_nservice_bus_as_concretion_nservice_bus_receives_as_interface()
    {
        ToExternalInterfaceMessageConsumer.Received.Clear();

        var waiter = ToExternalInterfaceMessageConsumer.WaitForReceipt();

        var id = Guid.NewGuid();
        await theFixture.Wolverine.SendAsync(new ConcreteToExternalMessage { Id = id });

        await waiter;

        ToExternalInterfaceMessageConsumer.Received.Single()
            .Id.ShouldBe(id);
    }
}

public class ConcreteToExternalMessage : IToExternalMessage
{
    public Guid Id { get; set; }
}