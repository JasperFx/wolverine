using System;

namespace InteropMessages
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

    public class ToMassTransit
    {
        public Guid Id { get; set; }
    }
}
