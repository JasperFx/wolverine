using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Wolverine.Grpc;

/// <summary>
///     Pluggable contributor that converts a .NET exception thrown inside a Wolverine-backed gRPC
///     service method into a rich <see cref="Google.Rpc.Status"/> with packed detail messages
///     (e.g. <see cref="Google.Rpc.BadRequest"/>, <see cref="Google.Rpc.ErrorInfo"/>).
///     The resulting status is transported to the client via the <c>grpc-status-details-bin</c>
///     trailer — the cross-language standard catalogued in
///     <see href="https://google.aip.dev/193#error_model">AIP-193</see>.
/// </summary>
/// <remarks>
///     <para>
///         Providers are resolved from the per-request DI scope as
///         <see cref="IEnumerable{T}"/> and evaluated in registration order. The first provider
///         to return a non-null <see cref="Google.Rpc.Status"/> wins — remaining providers are
///         not consulted and detail payloads are not merged across providers. Returning
///         <c>null</c> passes through to the next provider; if every provider returns null,
///         the interceptor falls back to the default <see cref="WolverineGrpcExceptionMapper"/>
///         table (AIP-193 §3.11).
///     </para>
///     <para>
///         Rich details are purely additive and opt-in — register providers via
///         <c>opts.UseGrpcRichErrorDetails(...)</c>. The default table stays authoritative
///         for every exception no provider claims.
///     </para>
/// </remarks>
public interface IGrpcStatusDetailsProvider
{
    /// <summary>
    ///     Build a rich <see cref="Google.Rpc.Status"/> for the given exception, or return
    ///     <c>null</c> to defer to the next provider / default mapping.
    /// </summary>
    /// <param name="exception">The exception thrown by a Wolverine handler or the gRPC adapter.</param>
    /// <param name="context">The current <see cref="ServerCallContext"/>.</param>
    Status? BuildStatus(Exception exception, ServerCallContext context);
}
