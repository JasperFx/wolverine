using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Http;
using Wolverine.Http.CodeGen;
using Wolverine.Runtime;

namespace WolverineWebApi.Samples;

#region sample_NowParameterStrategy

public class NowParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.Name == "now" && parameter.ParameterType == typeof(DateTimeOffset))
        {
            // This is tying into Wolverine's code generation model
            variable = new Variable(typeof(DateTimeOffset),
                $"{typeof(DateTimeOffset).FullNameInCode()}.{nameof(DateTimeOffset.UtcNow)}");
            return true;
        }

        variable = default;
        return false;
    }
}

#endregion