using System.ServiceModel;
using Grpc.Core;
using ProtoBuf;
using ProtoBuf.Grpc;
using Wolverine.Grpc.MultiTenancy;

namespace Wolverine.Grpc.Tests.MultiTenancy;

/// <summary>
///     Code-first gRPC contract marked for Wolverine code generation
///     (<see cref="WolverineGrpcServiceAttribute"/> on the interface — the
///     <see cref="CodeFirstGrpcServiceChain"/> path). The generated implementation is where the
///     GH-3368 tenant detection frame is woven, so calls through this service prove the
///     codegen-level detection end-to-end: strategy output → <c>tenantId</c> codegen variable →
///     <c>bus.TenantId</c> → envelope → handler's <see cref="IMessageContext.TenantId"/>.
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface ITenantEchoService
{
    Task<TenantEchoReply> Echo(TenantEchoRequest request, CallContext context = default);
}

[ProtoContract]
public class TenantEchoRequest
{
}

[ProtoContract]
public class TenantEchoReply
{
    [ProtoMember(1)]
    public string? TenantId { get; set; }
}

/// <summary>
///     Echoes the tenant id the Wolverine handler actually observed — the observable end of the
///     detection pipeline.
/// </summary>
public static class TenantEchoHandler
{
    public static TenantEchoReply Handle(TenantEchoRequest request, IMessageContext context)
    {
        return new TenantEchoReply { TenantId = context.TenantId };
    }
}

/// <summary>
///     Custom strategy for the <c>DetectWith&lt;T&gt;()</c> tests — reads a bespoke metadata
///     header no built-in strategy knows about, proving user-supplied detection logic runs
///     inside the generated service wrapper.
/// </summary>
public class CustomHeaderTenantDetection : IGrpcTenantDetection
{
    public const string HeaderName = "x-custom-tenant-source";

    public ValueTask<string?> DetectTenant(ServerCallContext context)
    {
        var entry = context.RequestHeaders.FirstOrDefault(e =>
            !e.IsBinary && string.Equals(e.Key, HeaderName, StringComparison.OrdinalIgnoreCase));

        return new ValueTask<string?>(entry?.Value is { Length: > 0 } value ? $"custom-{value}" : null);
    }
}
