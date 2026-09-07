using Microsoft.AspNetCore.Builder;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

/// <summary>
///     The shared face of the three Wolverine-managed gRPC chain kinds — proto-first
///     (<see cref="GrpcServiceChain" />), code-first (<see cref="CodeFirstGrpcServiceChain" />) and
///     hand-written (<see cref="HandWrittenGrpcServiceChain" />) — for the things a convention wants
///     rather than the things codegen wants (GH-4383).
/// </summary>
/// <remarks>
///     <para>
///         The three chain types are <c>Chain&lt;TSelf, TAttribute&gt;</c> closures over different
///         attribute types and share no ancestor of their own, so a bootstrap hook that wants to reach
///         "every gRPC chain" needs somewhere to stand. This is it, and it is deliberately thin: an
///         <see cref="IChain" /> for the middleware pipeline, an
///         <see cref="IEndpointConventionBuilder" /> for ASP.NET endpoint metadata, and the service type
///         the chain was built from so a convention can tell one from another.
///     </para>
///     <para>
///         The <see cref="IEndpointConventionBuilder" /> half is what <see cref="HttpChain" />'s
///         equivalent has always had. Wolverine generates the type that gets mapped, so an
///         <c>[Authorize]</c> on the hand-written service class is invisible to the router and the only
///         place a policy was ever picked up was the <c>[ServiceContract]</c> interface — which pushed
///         <c>Microsoft.AspNetCore.Authorization</c>, and whatever holds the policy-name constants, onto
///         the wire-contract assembly that clients also reference. Conventions collected here are
///         drained onto the <c>GrpcServiceEndpointConventionBuilder</c> that
///         <c>MapGrpcService&lt;T&gt;</c> returns, so ASP.NET's own
///         <c>AuthorizationMiddleware</c> stays the enforcer and a fallback policy still backs the
///         endpoint up.
///     </para>
/// </remarks>
public interface IGrpcChain : IChain, IEndpointConventionBuilder
{
    /// <summary>
    ///     The type this chain was built from: the proto-first stub, the code-first
    ///     <c>[ServiceContract]</c> interface, or the hand-written service class. Distinct from the
    ///     generated type that is actually mapped.
    /// </summary>
    Type ServiceType { get; }

    /// <summary>
    ///     The generated type Wolverine maps as the ASP.NET gRPC endpoint, or null before code
    ///     generation has run for this chain — which is the case while policies are being applied.
    /// </summary>
    Type? GeneratedType { get; }
}
