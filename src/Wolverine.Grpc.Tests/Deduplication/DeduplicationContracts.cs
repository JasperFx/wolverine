using System.ServiceModel;
using Grpc.Core;
using ProtoBuf;
using ProtoBuf.Grpc;
using Wolverine.Attributes;

namespace Wolverine.Grpc.Tests.Deduplication;

/// <summary>
///     GH-4180. Code-first contract exercising logical deduplication over gRPC.
///
///     <para>
///     <c>[Deduplicated]</c> is on ONE method rather than the interface, which is the point of
///     resolving the requirement per RPC: a gRPC chain is a whole service, and "all of this service's
///     calls are deduplicated or none are" is rarely what anyone wants. <see cref="EchoUnguarded" />
///     is the control — same service, same generated type, no deduplication.
///     </para>
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface IDeduplicatedEchoService
{
    [Deduplicated]
    Task<DedupEchoReply> Echo(DedupEchoRequest request, CallContext context = default);

    Task<DedupEchoReply> EchoUnguarded(DedupEchoRequest request, CallContext context = default);

    [Deduplicated(Required = false)]
    Task<DedupEchoReply> EchoOptional(DedupEchoRequest request, CallContext context = default);
}

[ProtoContract]
public class DedupEchoRequest
{
    [ProtoMember(1)]
    public string? Name { get; set; }
}

[ProtoContract]
public class DedupEchoReply
{
    [ProtoMember(1)]
    public string? Name { get; set; }
}

/// <summary>
///     The observable end of the pipeline: what actually reached a handler, and how often.
/// </summary>
public static class DedupEchoHandler
{
    public static readonly List<string> Received = [];

    public static DedupEchoReply Handle(DedupEchoRequest request)
    {
        lock (Received)
        {
            Received.Add(request.Name ?? "");
        }

        return new DedupEchoReply { Name = request.Name };
    }
}
