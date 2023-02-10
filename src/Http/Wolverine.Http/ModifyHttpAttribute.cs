using System;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;

namespace Wolverine.Http;

/// <summary>
///     Base class for attributes that configure how an HTTP endpoint is handled by applying
///     middleware or error handling rules
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class ModifyHttpAttribute : Attribute, IModifyChain<HttpChain>
{
    /// <summary>
    ///     Called by Wolverine during bootstrapping before message handlers are generated and compiled
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="rules"></param>
    public abstract void Modify(HttpChain chain, GenerationRules rules);
}