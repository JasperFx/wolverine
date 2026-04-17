using System.ServiceModel;
using FluentValidation;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace GreeterWithGrpcErrors.Messages;

/// <summary>
///     Code-first gRPC contract shared between <c>Server</c> and <c>Client</c>.
///     <c>Greet</c> exercises the FluentValidation → <c>BadRequest</c> path;
///     <c>Farewell</c> exercises the inline <c>MapException</c> → <c>PreconditionFailure</c>
///     path for a domain exception.
/// </summary>
[ServiceContract]
public interface IGreeterService
{
    Task<GreetReply> Greet(GreetRequest request, CallContext context = default);

    Task<FarewellReply> Farewell(FarewellRequest request, CallContext context = default);
}

[ProtoContract]
public class GreetRequest
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
    [ProtoMember(2)] public int Age { get; set; }
}

[ProtoContract]
public class GreetReply
{
    [ProtoMember(1)] public string Message { get; set; } = string.Empty;
}

[ProtoContract]
public class FarewellRequest
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
}

[ProtoContract]
public class FarewellReply
{
    [ProtoMember(1)] public string Message { get; set; } = string.Empty;
}

public class GreetRequestValidator : AbstractValidator<GreetRequest>
{
    public GreetRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(x => x.Age).GreaterThan(0).WithMessage("Age must be positive");
    }
}
