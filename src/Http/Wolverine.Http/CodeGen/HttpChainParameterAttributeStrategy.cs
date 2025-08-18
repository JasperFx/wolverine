using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class HttpChainParameterAttributeStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.TryGetAttribute<HttpChainParameterAttribute>(out var att))
        {
            variable = att.Modify(chain, parameter, container);
            return true;
        }

        if (parameter.TryGetAttribute<WolverineParameterAttribute>(out var att2))
        {
            variable = att2.Modify(chain, parameter, container, container.GetInstance<WolverineOptions>().CodeGeneration);
            return true;
        }

        variable = default;
        return false;
    }
}