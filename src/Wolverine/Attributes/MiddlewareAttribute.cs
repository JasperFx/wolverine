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

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        var applications = _frameTypes.Select(type => new MiddlewarePolicy.Application(chain, type, _ => true)).ToList();
        MiddlewarePolicy.ApplyToChain(applications, rules, chain);
    }
}