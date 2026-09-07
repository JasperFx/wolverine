using Microsoft.AspNetCore.Builder;

namespace Wolverine.Grpc;

// GH-4383. The IGrpcChain half of the three chain kinds, kept together and out of the codegen files
// for the same reason HttpChain.EndpointBuilder.cs exists: what a chain says to ASP.NET is a
// different concern from what it emits, and the three implementations are the same ten lines.
//
// Partial classes rather than a shared base: the three close Chain<TSelf, TAttribute> over different
// attribute types, so there is no ancestor of their own to put this on, and an interface is what
// they can actually share.

public partial class GrpcServiceChain : IGrpcChain
{
    private readonly GrpcEndpointConventions _endpointConventions = new();

    /// <inheritdoc />
    /// <remarks>The proto-first stub type this chain wraps.</remarks>
    Type IGrpcChain.ServiceType => StubType;

    /// <summary>
    ///     Add an ASP.NET endpoint convention to this service's generated gRPC endpoint. Applied when
    ///     the generated type is mapped, so <c>RequireAuthorization()</c> here reaches the router
    ///     rather than the wire contract.
    /// </summary>
    public void Add(Action<EndpointBuilder> convention) => _endpointConventions.Add(convention);

    /// <summary>Add a convention that runs after every convention added with <see cref="Add" />.</summary>
    public void Finally(Action<EndpointBuilder> convention) => _endpointConventions.Finally(convention);

    internal void ApplyEndpointConventions(IEndpointConventionBuilder builder)
        => _endpointConventions.ApplyTo(builder);
}

public partial class CodeFirstGrpcServiceChain : IGrpcChain
{
    private readonly GrpcEndpointConventions _endpointConventions = new();

    /// <inheritdoc />
    /// <remarks>The <c>[ServiceContract]</c> interface this chain implements.</remarks>
    Type IGrpcChain.ServiceType => ServiceContractType;

    /// <summary>
    ///     Add an ASP.NET endpoint convention to this service's generated gRPC endpoint. Applied when
    ///     the generated type is mapped, so <c>RequireAuthorization()</c> here reaches the router
    ///     rather than the wire contract.
    /// </summary>
    public void Add(Action<EndpointBuilder> convention) => _endpointConventions.Add(convention);

    /// <summary>Add a convention that runs after every convention added with <see cref="Add" />.</summary>
    public void Finally(Action<EndpointBuilder> convention) => _endpointConventions.Finally(convention);

    internal void ApplyEndpointConventions(IEndpointConventionBuilder builder)
        => _endpointConventions.ApplyTo(builder);
}

public partial class HandWrittenGrpcServiceChain : IGrpcChain
{
    private readonly GrpcEndpointConventions _endpointConventions = new();

    /// <inheritdoc />
    /// <remarks>The user's concrete service class, not the generated delegation wrapper.</remarks>
    Type IGrpcChain.ServiceType => ServiceClassType;

    /// <summary>
    ///     Add an ASP.NET endpoint convention to this service's generated gRPC endpoint. Applied when
    ///     the generated type is mapped, so an <c>[Authorize]</c> policy no longer has to live on the
    ///     <c>[ServiceContract]</c> interface to be seen by the router.
    /// </summary>
    public void Add(Action<EndpointBuilder> convention) => _endpointConventions.Add(convention);

    /// <summary>Add a convention that runs after every convention added with <see cref="Add" />.</summary>
    public void Finally(Action<EndpointBuilder> convention) => _endpointConventions.Finally(convention);

    internal void ApplyEndpointConventions(IEndpointConventionBuilder builder)
        => _endpointConventions.ApplyTo(builder);
}
