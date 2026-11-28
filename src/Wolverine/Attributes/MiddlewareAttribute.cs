using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Runtime;

namespace Wolverine.Attributes;

/// <summary>
///     Attach one or more Wolverine conventional middleware types
/// </summary>
public class MiddlewareAttribute : ModifyChainAttribute
{
    private readonly Type[] _frameTypes;

    public MiddlewareAttribute(params Type[] frameTypes)
    {
        _frameTypes = frameTypes;
    }

    /// <summary>
    /// Use this to optionally restrict the application of this middleware to only message handlers
    /// or HTTP endpoints. Default is Anywhere
    /// </summary>
    public MiddlewareScoping Scoping { get; set; } = MiddlewareScoping.Anywhere;

    // GH-6013. A Type[] cannot carry [DynamicallyAccessedMembers], and a typeof() in an attribute argument roots
    // the type but not its methods' reflection metadata, so this is the one middleware registration path the trim
    // analyzer cannot vouch for. A Native AOT application that uses [Middleware(typeof(T))] has to root T's public
    // methods itself, with a [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(T))].
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "The middleware types come from the attribute's own typeof() arguments in a Type[], which cannot be annotated; a native image that uses [Middleware] roots the types' public methods with [DynamicDependency]. See GH-6013.")]
    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        var applications = _frameTypes.Select(type => new MiddlewarePolicy.Application(chain, type, _ => true)).ToList();
        MiddlewarePolicy.ApplyToChain(applications, rules, chain);
    }
}