using System;

namespace MassTransitService
{
    public class InitialMessage
    {
        public Guid Id { get; set; }
    }

    public class ResponseMessage
    {
        public Guid Id { get; set; }
    }

    public class ToWolverine
    {
        public Guid Id { get; set; }
    }

    public class ToExternal
    {
        public Guid Id { get; set; }
    }
}
