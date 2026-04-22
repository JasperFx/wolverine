using Wolverine.Grpc.Tests.GrpcMiddlewareScoping;
using Wolverine.Grpc.Tests.GrpcValidateConvention.Generated;

namespace Wolverine.Grpc.Tests.GrpcValidateConvention;

public static class ValidateGreetHandler
{
    public const string Marker = "ValidateGreetHandler.Handle";

    public static ValidateGreetReply Handle(ValidateGreetRequest request, MiddlewareInvocationSink sink)
    {
        sink.Record(Marker);
        return new ValidateGreetReply { Message = $"Hello, {request.Name}" };
    }
}
