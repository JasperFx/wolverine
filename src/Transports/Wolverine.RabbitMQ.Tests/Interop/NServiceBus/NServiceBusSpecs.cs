using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus;
using NServiceBusRabbitMqService;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Interop.NServiceBus;

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


        var session = await theFixture.Wolverine.ExecuteAndWaitAsync(async () =>
        {
            var sender = theFixture.NServiceBus.Services.GetRequiredService<IMessageSession>();
            await sender.Publish(new ResponseMessage { Id = id });
        }, 60000);

        var envelope = ResponseHandler.Received.FirstOrDefault();
        envelope.Message.ShouldBeOfType<ResponseMessage>().Id.ShouldBe(id);
        envelope.ShouldNotBeNull();

        envelope.CorrelationId.ShouldNotBeNull();
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
}