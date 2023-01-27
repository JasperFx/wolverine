using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Http.CodeGen;

internal class ReadStringQueryStringValue : SyncFrame
{
    public ReadStringQueryStringValue(string name)
    {
        Variable = new Variable(typeof(string), name, this);
    }
    
    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"string {Variable.Usage} = httpContext.Request.Query[\"{Variable.Usage}\"].FirstOrDefault();");

        Next?.GenerateCode(method, writer);
    }
}

internal class QueryStringParameterStrategy : IParameterStrategy
{
    public bool TryMatch(EndpointChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        variable = null;
        
        if (parameter.ParameterType == typeof(string))
        {
            variable = new ReadStringQueryStringValue(parameter.Name).Variable;
            return true;
        }

        return false;
    }
}