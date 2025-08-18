using System.Diagnostics;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Attributes;

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
    
    /// <summary>
    /// Where should the identity value for resolving this parameter come from?
    /// Default is a named member on the message type or HTTP request type (if one exists)
    /// </summary>
    public ValueSource ValueSource { get; set; } = ValueSource.InputMember;

    /// <summary>
    ///     Called by Wolverine during bootstrapping to modify the code generation
    /// for an HTTP endpoint with the decorated parameter
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="parameter"></param>
    /// <param name="container"></param>
    /// <param name="rules"></param>
    public abstract Variable Modify(IChain chain, ParameterInfo parameter,
        IServiceContainer container, GenerationRules rules);

    internal static void TryApply(MethodCall call, IServiceContainer container, GenerationRules rules, IChain chain)
    {
        var parameters = call.Method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].TryGetAttribute<WolverineParameterAttribute>(out var att))
            {
                var variable = att.Modify(chain, parameters[i], container, rules);
                call.Arguments[i] = variable;
            }
        }
    }

    protected bool tryFindIdentityVariable(IChain chain, ParameterInfo parameter, Type idType, out Variable variable)
    {
        if (ArgumentName.IsNotEmpty())
        {
            if (chain.TryFindVariable(ArgumentName, ValueSource, idType, out variable))
            {
                return true;
            }
        }
        
        if (chain.TryFindVariable(parameter.ParameterType.Name + "Id", ValueSource, idType, out variable))
        {
            return true;
        }
        
        if (chain.TryFindVariable("Id", ValueSource, idType, out variable))
        {
            return true;
        }

        variable = default;
        return false;
    }
}