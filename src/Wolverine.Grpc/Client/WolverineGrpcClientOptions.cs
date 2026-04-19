using Grpc.Core;
using Grpc.Net.Client;

namespace Wolverine.Grpc.Client;

/// <summary>
///     Wolverine-specific configuration for a typed gRPC client registered via
///     <see cref="WolverineGrpcClientExtensions.AddWolverineGrpcClient{T}(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{WolverineGrpcClientOptions}?)"/>.
/// </summary>
/// <remarks>
///     <para>
///         This type layers on top of the Microsoft <see cref="Grpc.Net.ClientFactory.GrpcClientFactoryOptions"/> —
///         it does not replace it. The underlying <see cref="GrpcChannelOptions"/> remain fully available via
///         <see cref="WolverineGrpcClientBuilder.ConfigureChannel"/>, so any knob exposed by
///         <c>Grpc.Net.Client</c> stays reachable.
///     </para>
///     <para>
///         Each registered typed client has its own named options instance, keyed by
///         <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> on the typed-client name
///         (which matches <c>IHttpClientFactory</c>'s keying scheme).
///     </para>
/// </remarks>
public sealed class WolverineGrpcClientOptions
{
    /// <summary>
    ///     The server base address. Required; the extension throws at resolution time if it is <c>null</c>.
    /// </summary>
    public Uri? Address { get; set; }

    /// <summary>
    ///     Whether the client should stamp Wolverine envelope headers (<c>correlation-id</c>, <c>tenant-id</c>,
    ///     <c>parent-id</c>, <c>conversation-id</c>, <c>message-id</c>) on outgoing calls when an
    ///     <see cref="IMessageContext"/> is resolvable from the current DI scope. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    ///     The interceptor never overwrites a header the caller already set via
    ///     <see cref="Metadata"/>. It also silently no-ops when no <see cref="IMessageContext"/> is in
    ///     scope (e.g. a bare <c>Program.cs</c> caller) — turning this off is only needed when a
    ///     <see cref="IMessageContext"/> is resolvable but propagation is not wanted.
    /// </remarks>
    public bool PropagateEnvelopeHeaders { get; set; } = true;

    /// <summary>
    ///     Optional per-client override for <see cref="RpcException"/> → .NET exception mapping.
    ///     Consulted before the default <see cref="WolverineGrpcExceptionMapper.MapToException"/> table.
    ///     Return <c>null</c> to fall through to the default mapping.
    /// </summary>
    public Func<RpcException, Exception?>? MapRpcException { get; set; }
}
