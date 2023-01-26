using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Lamar;

namespace Wolverine.Http.CodeGen;

internal class ReadStringRouteValue : SyncFrame
{
    public ReadStringRouteValue(string name)
    {
        Variable = new Variable(typeof(string), name, this);
    }
    
    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = (string)httpContext.GetRouteValue(\"{Variable.Usage}\");");
        Next?.GenerateCode(method, writer);
    }
}

internal class RouteParameterStrategy : IParameterStrategy
{
    public bool TryMatch(EndpointChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        var matches = chain.RoutePattern.Parameters.Any(x => x.Name == parameter.Name);
        if (matches)
        {
            if (parameter.ParameterType == typeof(string))
            {
                variable = new ReadStringRouteValue(parameter.Name).Variable;
                return true;
            }
            else
            {
                throw new NotImplementedException("come back to this");
            }
            
            
            
        }

        variable = null;
        return matches;
    }
}

