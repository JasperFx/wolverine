using System;
using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Middleware;

namespace Wolverine.Attributes;

/// <summary>
/// Attach one or more Wolverine conventional middleware types
/// </summary>
public class MiddlewareAttribute : ModifyChainAttribute
{
    private readonly Type[] _frameTypes;

    public MiddlewareAttribute(params Type[] frameTypes)
    {
        _frameTypes = frameTypes;
    }

    public override void Modify(IChain chain, GenerationRules rules, IContainer container)
    {
        var applications = _frameTypes.Select(type => new MiddlewarePolicy.Application(type, _ => true)).ToList();
        MiddlewarePolicy.ApplyToChain(applications, chain);
    }
}