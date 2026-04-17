using System.Runtime.Serialization;
using FluentValidation;
using ProtoBuf.Grpc.Configuration;

namespace Wolverine.Http.Grpc.Tests.RichErrors;

[DataContract]
public class GreetCommand
{
    [DataMember(Order = 1)] public string Name { get; set; } = string.Empty;
    [DataMember(Order = 2)] public int Age { get; set; }
}

[DataContract]
public class GreetReply
{
    [DataMember(Order = 1)] public string Message { get; set; } = string.Empty;
}

[Service]
public interface IRichErrorsGreeterService
{
    Task<GreetReply> Greet(GreetCommand request);
}

public class GreetCommandValidator : AbstractValidator<GreetCommand>
{
    public GreetCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(x => x.Age).GreaterThan(0).WithMessage("Age must be positive");
    }
}

public class RichErrorsGreeterGrpcService : WolverineGrpcServiceBase, IRichErrorsGreeterService
{
    public RichErrorsGreeterGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<GreetReply> Greet(GreetCommand request) => Bus.InvokeAsync<GreetReply>(request);
}

public static class RichErrorsGreeterHandler
{
    public static GreetReply Handle(GreetCommand command)
        => new() { Message = $"Hello, {command.Name}" };
}
