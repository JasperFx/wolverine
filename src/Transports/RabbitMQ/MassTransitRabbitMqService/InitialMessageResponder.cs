using MassTransit;

namespace MassTransitService;

public class InitialMessageResponder : IConsumer<InitialMessage>
{
    public Task Consume(ConsumeContext<InitialMessage> context)
    {
        var message = context.Message;
        var response = new ResponseMessage
        {
            Id = message.Id
        };

        return context.RespondAsync(response);
    }
}