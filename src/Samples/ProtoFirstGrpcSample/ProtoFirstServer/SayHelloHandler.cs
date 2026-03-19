using ProtoContracts;

namespace ProtoFirstServer;

/// <summary>
/// Standard Wolverine handler that processes <see cref="HelloRequest"/> messages
/// and returns a <see cref="HelloReply"/>.
///
/// This handler has no knowledge of gRPC — it is a plain Wolverine handler.
/// The <see cref="GreeterService"/> bridges the gRPC transport layer to this handler
/// by calling <c>Bus.InvokeAsync&lt;HelloReply&gt;(request)</c>.
/// </summary>
public class SayHelloHandler
{
    public static HelloReply Handle(HelloRequest request)
        => new HelloReply { Message = $"Hello, {request.Name}!" };
}
