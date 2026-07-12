using JasperFx;
using JasperFx.CodeGeneration;

namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     The configured tenant detection strategies for Wolverine-managed gRPC services, plus the
///     <see cref="IGrpcChainPolicy"/> that weaves a <see cref="DetectGrpcTenantIdFrame"/> into
///     every applicable gRPC chain during bootstrapping. The gRPC counterpart to Wolverine.Http's
///     <c>TenantIdDetection : ITenantDetectionPolicies, IHttpPolicy</c>. Registered as a policy by
///     the <see cref="WolverineGrpcOptions"/> constructor and exposed for configuration as
///     <see cref="WolverineGrpcOptions.TenantId"/>.
/// </summary>
internal class GrpcTenantIdDetection : IGrpcTenantDetectionPolicies, IGrpcChainPolicy
{
    private readonly WolverineGrpcOptions _parent;

    // DetectWith<T>() registrations are recorded as types and built from the application
    // container at policy-Apply time. Unlike Wolverine.Http — whose TenantId API is configured
    // inside MapWolverineEndpoints() when the container already exists — the gRPC options are
    // configured during AddWolverineGrpc() service registration, before any container is built,
    // so eager resolution is impossible here.
    private readonly List<Type> _deferredDetectionTypes = [];

    public GrpcTenantIdDetection(WolverineGrpcOptions parent)
    {
        _parent = parent;
    }

    public List<IGrpcTenantDetection> Strategies { get; } = [];

    public bool AssertTenantExists { get; private set; }

    /// <summary>
    ///     True when no strategy was configured explicitly and the zero-config default
    ///     (detect the <c>tenant-id</c> metadata header stamped by
    ///     <c>WolverineGrpcClientPropagationInterceptor</c>) was applied instead.
    /// </summary>
    public bool ZeroConfigDefaultApplied { get; private set; }

    public void IsRequestHeaderValue(string headerKey)
    {
        Strategies.Add(new MetadataHeaderDetection(headerKey));
    }

    public void IsClaimTypeNamed(string claimType)
    {
        Strategies.Add(new ClaimsPrincipalDetection(claimType));
    }

    public void AssertExists()
    {
        AssertTenantExists = true;
    }

    public void DefaultIs(string defaultTenantId)
    {
        Strategies.Add(new FallbackDefault(defaultTenantId));
    }

    public void DetectWith(IGrpcTenantDetection detection)
    {
        Strategies.Add(detection);
    }

    public void DetectWith<T>() where T : IGrpcTenantDetection
    {
        _deferredDetectionTypes.Add(typeof(T));
    }

    private bool hasExplicitConfiguration => Strategies.Count > 0 || _deferredDetectionTypes.Count > 0;

    void IGrpcChainPolicy.Apply(
        IReadOnlyList<GrpcServiceChain> protoFirstChains,
        IReadOnlyList<CodeFirstGrpcServiceChain> codeFirstChains,
        IReadOnlyList<HandWrittenGrpcServiceChain> handWrittenChains,
        GenerationRules rules,
        IServiceContainer container)
    {
        // Zero-config default: when the user never configured tenancy detection but the
        // server-side propagation interceptor is active, detect the same 'tenant-id' metadata
        // header the Wolverine gRPC client interceptor stamps on outgoing calls — so a
        // Wolverine-to-Wolverine hop round-trips the tenant id with no server configuration,
        // structurally in generated code rather than relying on the ambient IMessageContext.
        if (!hasExplicitConfiguration && _parent.PropagateEnvelopeHeaders)
        {
            Strategies.Add(new MetadataHeaderDetection(EnvelopeConstants.TenantIdKey));
            ZeroConfigDefaultApplied = true;
        }

        foreach (var type in _deferredDetectionTypes)
        {
            Strategies.Add((IGrpcTenantDetection)container.QuickBuild(type));
        }

        _deferredDetectionTypes.Clear();

        // Mirrors Wolverine.Http: no strategies (even with AssertExists()) means no frames.
        if (Strategies.Count == 0)
        {
            return;
        }

        foreach (var chain in protoFirstChains)
        {
            chain.TenantDetection = this;
        }

        foreach (var chain in codeFirstChains)
        {
            chain.TenantDetection = this;
        }

        foreach (var chain in handWrittenChains)
        {
            chain.TenantDetection = this;
        }
    }
}
