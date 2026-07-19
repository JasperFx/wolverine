using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;
using Wolverine.Grpc;

namespace GreeterCodeFirstGrpc.Messages;

/// <summary>
///     Code-first gRPC contract. Annotating the <em>interface</em> with
///     <c>[WolverineGrpcService]</c> tells Wolverine to generate the concrete
///     implementation at startup — no service class is written by hand.
///     <c>[ServiceContract]</c> is required by protobuf-net.Grpc for routing.
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface IGreeterCodeFirstService
{
    Task<GreetReply> Greet(GreetRequest request, CallContext context = default);
    IAsyncEnumerable<GreetReply> StreamGreetings(StreamGreetingsRequest request, CallContext context = default);

    // Client streaming: protobuf-net.Grpc hands the generated implementation the inbound
    // stream as IAsyncEnumerable<GreetRequest>, which Wolverine forwards straight to
    // IMessageBus.StreamAsync<GreetRequest, GreetingSummary> for a single folded reply.
    Task<GreetingSummary> CollectGreetings(IAsyncEnumerable<GreetRequest> requests, CallContext context = default);
}

[ProtoContract]
public class GreetRequest
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
}

[ProtoContract]
public class StreamGreetingsRequest
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
    [ProtoMember(2)] public int Count { get; set; }
}

[ProtoContract]
public class GreetReply
{
    [ProtoMember(1)] public string Message { get; set; } = string.Empty;
}

[ProtoContract]
public class GreetingSummary
{
    [ProtoMember(1)] public string Message { get; set; } = string.Empty;
    [ProtoMember(2)] public int Count { get; set; }
}
