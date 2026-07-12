using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     Code-first gRPC contract whose server implementation forwards a saga message to the
///     Wolverine bus. Used to characterize what happens when a <em>header-identified</em> saga
///     (one whose id is resolved from the envelope <c>saga-id</c> header, not the message body)
///     is driven over a gRPC service hop — see <see cref="CountingSaga"/> for why this message
///     deliberately carries no id member.
/// </summary>
[ServiceContract]
public interface ICountingSagaService
{
    Task<StartCountingReply> Start(StartCountingRequest request, CallContext context = default);
}

/// <summary>
///     Saga start message with <b>no</b> id-like member — no <c>Id</c>, <c>SagaId</c>,
///     <c>CountingSagaId</c>, nor <c>[SagaIdentity]</c>. This forces
///     <c>SagaChain.DetermineSagaIdMember</c> to return null, which routes identity resolution onto
///     the envelope <c>saga-id</c> header (<c>PullSagaIdFromEnvelopeFrame</c>) instead of the
///     message body.
/// </summary>
[ProtoContract]
public class StartCountingRequest
{
    [ProtoMember(1)]
    public string? Label { get; set; }
}

[ProtoContract]
public class StartCountingReply
{
    [ProtoMember(1)]
    public string? SagaId { get; set; }
}
