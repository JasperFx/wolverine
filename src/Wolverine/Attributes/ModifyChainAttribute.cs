using JasperFx;
using JasperFx.CodeGeneration;
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

#region sample_valuesource
public enum ValueSource
{
    /// <summary>
    /// This value can be sourced by any mechanism that matches the name. This is the default.
    /// </summary>
    Anything,
    
    /// <summary>
    /// The value should be sourced by a property or field on the message type or HTTP request type
    /// </summary>
    InputMember,
    
    /// <summary>
    /// The value should be sourced by a route argument of an HTTP request
    /// </summary>
    RouteValue,
    
    /// <summary>
    /// The value should be sourced by a query string parameter of an HTTP request
    /// </summary>
    FromQueryString,

    /// <summary>
    /// The value should be sourced by an HTTP request header or an Envelope header in message handlers
    /// </summary>
    Header,

    /// <summary>
    /// The value should be sourced from a claim on the ClaimsPrincipal. Only supported in HTTP endpoints.
    /// </summary>
    Claim,

    /// <summary>
    /// The value should be sourced from the return value of a named static method on the handler or endpoint class
    /// </summary>
    Method
}

#endregion