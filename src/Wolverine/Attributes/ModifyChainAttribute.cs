using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Attributes;

/// <summary>
///     Base class to use for applying middleware or other alterations to generic
///     IChains (either RouteChain or HandlerChain)
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class ModifyChainAttribute : Attribute
{
    public abstract void Modify(IChain chain, GenerationRules rules, IServiceContainer container);
}

/// <summary>
/// Base class for any attributes on parameters to Wolverine message handler or HTTP endpoint
/// methods that modifies the codegen for that handler
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public abstract class WolverineParameterAttribute : Attribute
{
    protected WolverineParameterAttribute()
    {
    }

    protected WolverineParameterAttribute(string argumentName)
    {
        ArgumentName = argumentName;
    }

    public string ArgumentName { get; set; }
    public ValueSource ArgumentSource { get; set; } = ValueSource.Anything;
    
    /// <summary>
    ///     Called by Wolverine during bootstrapping to modify the code generation
    /// for an HTTP endpoint with the decorated parameter
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="parameter"></param>
    /// <param name="container"></param>
    public abstract Variable Modify(IChain chain, ParameterInfo parameter,
        IServiceContainer container);
}

public enum ValueSource
{
    Anything,
    InputMember,
    RouteValue,
    QueryString,
    Claim,
    Header
}