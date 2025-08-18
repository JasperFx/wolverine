using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class FromServicesParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        variable = default!;
        if (parameter.HasAttribute<FromServicesAttribute>() || parameter.HasAttribute<NotBodyAttribute>())
        {
            // No variable here, that will happen later in the compilation
            // process
            return true;
        }

        return false;
    }
}