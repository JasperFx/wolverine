using Grpc.Core;
using ProtoBuf.Grpc;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     Code-first gRPC service that reads the five envelope-identity headers Wolverine's
///     propagation interceptor stamps and returns them in the reply so tests can assert
///     which headers survived the hop.
/// </summary>
public class HeaderEchoGrpcService : IHeaderEchoService
{
    public Task<HeaderEchoReply> Echo(HeaderEchoRequest request, CallContext context = default)
    {
        var headers = context.ServerCallContext?.RequestHeaders;

        return Task.FromResult(new HeaderEchoReply
        {
            CorrelationId = Find(headers, EnvelopeConstants.CorrelationIdKey),
            TenantId = Find(headers, EnvelopeConstants.TenantIdKey),
            ParentId = Find(headers, EnvelopeConstants.ParentIdKey),
            ConversationId = Find(headers, EnvelopeConstants.ConversationIdKey),
            MessageId = Find(headers, EnvelopeConstants.IdKey)
        });
    }

    private static string? Find(Metadata? headers, string key)
    {
        if (headers == null) return null;

        foreach (var entry in headers)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }
}
