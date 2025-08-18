using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

public class FromHeaderStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        var att = parameter.GetCustomAttributes().OfType<IFromHeaderMetadata>().FirstOrDefault();

        if (att != null)
        {
            variable = chain.GetOrCreateHeaderVariable(att, parameter);
            return true;
        }

        variable = default;
        return false;
    }
}
