using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Grpc;

/// <summary>
///     Server-side gRPC interceptor that reads the <c>correlation-id</c> and <c>tenant-id</c>
///     envelope headers off an inbound call and applies them to the ambient, request-scoped
///     <see cref="IMessageContext"/> before the service method runs. The client-side counterpart is
///     <see cref="Client.WolverineGrpcClientPropagationInterceptor"/> — this is what actually reads
///     those headers back on the receiving end of a Wolverine-to-Wolverine hop.
/// </summary>
/// <remarks>
///     <para>
///         Only <c>correlation-id</c> and <c>tenant-id</c> are round-tripped this way.
///         <see cref="IMessageContext.CorrelationId"/> and <see cref="IMessageBus.TenantId"/> are the
///         only envelope identifiers that can be set on the ambient context <em>before</em> a message
///         has been invoked — <c>parent-id</c> and <c>conversation-id</c> only exist on an
///         <see cref="Envelope"/>, and there is no envelope yet at this point (<see
///         cref="IMessageContext.Envelope"/> is null outside of message handling). Once the service
///         method calls <c>Bus.InvokeAsync(...)</c>, Wolverine copies the now-set
///         <c>CorrelationId</c>/<c>TenantId</c> onto the freshly created envelope automatically — no
///         further wiring needed, and Marten/Polecat tenant-scoped session frames pick it up the same
///         way they would for any other transport.
///     </para>
///     <para>
///         Cross-process parent/trace correlation for gRPC hops is handled separately by
///         OpenTelemetry <c>Activity</c> propagation over HTTP/2 (W3C traceparent) — see
///         <a href="https://wolverine.netlify.app/guide/grpc/handlers.html">How gRPC Handlers Work</a>.
///     </para>
///     <para>
///         Both fields are set unconditionally when the corresponding header is present — the same
///         convention every other Wolverine transport uses for a freshly received message (see
///         <c>MessageContext.ReadEnvelope</c>: <c>CorrelationId = originalEnvelope.CorrelationId</c>,
///         no "only if empty" check). This matters for <c>correlation-id</c> specifically: a brand
///         new <see cref="IMessageBus"/>/<see cref="IMessageContext"/> already has a non-empty,
///         auto-generated <see cref="IMessageContext.CorrelationId"/> (seeded from
///         <see cref="System.Diagnostics.Activity.Current"/>'s root id, or a fresh GUID) before this
///         interceptor ever runs — gating on "currently empty" would silently never apply an inbound
///         header.
///     </para>
///     <para>
///         Registered automatically by <see
///         cref="WolverineGrpcExtensions.AddWolverineGrpc(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>,
///         and runs inside <see cref="WolverineGrpcExceptionInterceptor"/> so an exception it throws
///         is still translated, symmetric to how propagation sits inside exception translation on the
///         client.
///     </para>
/// </remarks>
public sealed class WolverineGrpcServicePropagationInterceptor : Interceptor
{
    private readonly WolverineGrpcOptions _options;

    public WolverineGrpcServicePropagationInterceptor(WolverineGrpcOptions options)
    {
        _options = options;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Propagate(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Propagate(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Propagate(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Propagate(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Propagate(ServerCallContext context)
    {
        if (!_options.PropagateEnvelopeHeaders) return;

        var services = context.GetHttpContext().RequestServices;

        // IMessageContext is a scoped service — bail cleanly when there is no active Wolverine scope.
        var ctx = services.GetService(typeof(IMessageContext)) as IMessageContext;
        if (ctx == null) return;

        var correlationId = Find(context.RequestHeaders, EnvelopeConstants.CorrelationIdKey);
        if (!string.IsNullOrEmpty(correlationId))
        {
            ctx.CorrelationId = correlationId;
        }

        var tenantId = Find(context.RequestHeaders, EnvelopeConstants.TenantIdKey);
        if (!string.IsNullOrEmpty(tenantId))
        {
            ctx.TenantId = tenantId;
        }
    }

    private static string? Find(Metadata headers, string key)
    {
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
