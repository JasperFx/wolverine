using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     Code-first gRPC contract whose server implementation reads selected metadata headers off
///     <see cref="CallContext"/> and echoes them back to the caller. Used by the propagation
///     interceptor tests to observe exactly what the client-side interceptor stamped on the wire.
/// </summary>
[ServiceContract]
public interface IHeaderEchoService
{
    Task<HeaderEchoReply> Echo(HeaderEchoRequest request, CallContext context = default);
}

[ProtoContract]
public class HeaderEchoRequest
{
    [ProtoMember(1)]
    public string Noise { get; set; } = string.Empty;
}

[ProtoContract]
public class HeaderEchoReply
{
    [ProtoMember(1)]
    public string? CorrelationId { get; set; }

    [ProtoMember(2)]
    public string? TenantId { get; set; }

    [ProtoMember(3)]
    public string? ParentId { get; set; }

    [ProtoMember(4)]
    public string? ConversationId { get; set; }

    [ProtoMember(5)]
    public string? MessageId { get; set; }
}
