using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

#region sample_IParameterStrategy

/// <summary>
/// Apply custom handling to a Wolverine.Http endpoint/chain based on a parameter within the
/// implementing Wolverine http endpoint method
/// </summary>
/// <param name="variable">The Variable referring to the input of this parameter</param>
public interface IParameterStrategy
{
    bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable);
}

#endregion