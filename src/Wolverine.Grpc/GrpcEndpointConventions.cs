using Microsoft.AspNetCore.Builder;

namespace Wolverine.Grpc;

/// <summary>
///     The ASP.NET endpoint conventions a gRPC chain has collected, and the drain onto the real
///     endpoint builder once <c>MapGrpcService&lt;T&gt;</c> has produced one (GH-4383).
/// </summary>
/// <remarks>
///     Shared by all three chain kinds rather than inherited, because they have no ancestor of their
///     own to put it on. The <c>Finally</c> pass mirrors
///     <see cref="IEndpointConventionBuilder.Finally" />: conventions that must observe everything the
///     ordinary pass added run after all of it, whichever order the two were registered in.
/// </remarks>
internal sealed class GrpcEndpointConventions
{
    private readonly List<Action<EndpointBuilder>> _conventions = new();
    private readonly List<Action<EndpointBuilder>> _finallyConventions = new();

    public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

    public void Finally(Action<EndpointBuilder> convention) => _finallyConventions.Add(convention);

    public void ApplyTo(IEndpointConventionBuilder builder)
    {
        foreach (var convention in _conventions) builder.Add(convention);
        foreach (var convention in _finallyConventions) builder.Finally(convention);
    }
}
