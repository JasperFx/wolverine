using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;

namespace Wolverine.Http.CodeGen;

internal class HttpChainParameterAttributeStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.TryGetAttribute<HttpChainParameterAttribute>(out var att))
        {
            
        }

        variable = default;
        return false;
    }
}