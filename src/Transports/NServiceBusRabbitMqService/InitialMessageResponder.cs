using NServiceBus;

namespace NServiceBusService
{
    public class InitialMessageResponder : IHandleMessages<InitialMessage>
    {
        public Task Handle(InitialMessage message, IMessageHandlerContext context)
        {
            var response = new ResponseMessage
            {
                Id = message.Id
            };

            return context.Reply(response);
        }
    }
}
