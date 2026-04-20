using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace Wolverine.Grpc.Client;

/// <summary>
///     Client-side gRPC <see cref="Interceptor"/> that stamps Wolverine envelope identity headers
///     (<c>correlation-id</c>, <c>tenant-id</c>, <c>parent-id</c>, <c>conversation-id</c>,
///     <c>message-id</c>) onto outgoing calls when an <see cref="IMessageContext"/> is resolvable
///     from the current DI scope.
/// </summary>
/// <remarks>
///     <para>
///         Registered automatically for every typed client created via
///         <see cref="WolverineGrpcClientExtensions.AddWolverineGrpcClient{T}(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{WolverineGrpcClientOptions}?)"/>.
///         Symmetric to how <see cref="Wolverine.Transports.EnvelopeMapper{TIncoming,TOutgoing}"/>
///         propagates the same fields across every other Wolverine transport — the wire vocabulary
///         (kebab-case keys from <see cref="EnvelopeConstants"/>) is identical.
///     </para>
///     <para>
///         This interceptor silently no-ops when no <see cref="IMessageContext"/> is in scope — a
///         bare <c>Program.cs</c> caller with no Wolverine runtime has nothing to propagate, which is
///         the correct default. Likewise it never overwrites a header the caller has already set
///         via the call's <see cref="Metadata"/> (per-call overrides win over propagation).
///     </para>
/// </remarks>
public sealed class WolverineGrpcClientPropagationInterceptor : Interceptor
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<WolverineGrpcClientOptions> _options;
    private readonly string _name;

    public WolverineGrpcClientPropagationInterceptor(
        IServiceProvider services,
        IOptionsMonitor<WolverineGrpcClientOptions> options,
        string name)
    {
        _services = services;
        _options = options;
        _name = name;
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, Propagate(context));
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, Propagate(context));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, Propagate(context));
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(Propagate(context));
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(Propagate(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> Propagate<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var opts = _options.Get(_name);
        if (!opts.PropagateEnvelopeHeaders)
        {
            return context;
        }

        // IMessageContext is a scoped service — bail cleanly when there is no active Wolverine scope.
        var ctx = _services.GetService(typeof(IMessageContext)) as IMessageContext;
        if (ctx == null)
        {
            return context;
        }

        var existing = context.Options.Headers;
        var headers = existing ?? new Metadata();
        var stamped = false;

        stamped |= TryStamp(headers, EnvelopeConstants.CorrelationIdKey, ctx.CorrelationId);
        stamped |= TryStamp(headers, EnvelopeConstants.TenantIdKey, ctx.TenantId);

        if (ctx.Envelope is { } envelope)
        {
            stamped |= TryStamp(headers, EnvelopeConstants.IdKey, envelope.Id.ToString());
            stamped |= TryStamp(headers, EnvelopeConstants.ParentIdKey, envelope.ParentId);
            stamped |= TryStamp(headers, EnvelopeConstants.ConversationIdKey,
                envelope.ConversationId == Guid.Empty ? null : envelope.ConversationId.ToString());
        }

        // When existing headers were present we mutated them in place — nothing to rewrite.
        // Only reconstruct the CallOptions when we allocated a fresh Metadata and actually used it.
        if (existing != null || !stamped)
        {
            return context;
        }

        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            context.Options.WithHeaders(headers));
    }

    private static bool TryStamp(Metadata headers, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Per-call overrides win — never clobber a header the caller already set.
        foreach (var entry in headers)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        headers.Add(key, value);
        return true;
    }
}
