using NServiceBus;

namespace NServiceBusService
{
    public class InitialMessage : ICommand
    {
        public Guid Id { get; set; }
    }

    public class ResponseMessage : IMessage
    {
        public Guid Id { get; set; }
    }

    public class ToWolverine: ICommand
    {
        public Guid Id { get; set; }
    }

    public class ToExternal: ICommand
    {
        public Guid Id { get; set; }
    }
}
