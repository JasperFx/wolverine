using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.CodeGen;

internal class ReadJsonBody : AsyncFrame
{
    public ReadJsonBody(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name, this);
    }
    
    public Variable Variable { get; }
    
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var ({Variable.Usage}, jsonContinue) = await ReadJsonAsync<{Variable.VariableType.FullNameInCode()}>(httpContext);");
        writer.Write($"if (jsonContinue == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)}) return;");
        
        Next?.GenerateCode(method, writer);
    }
}

internal class JsonBodyParameterStrategy : IParameterStrategy
{
    public bool TryMatch(EndpointChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        variable = default;

        if (chain.HttpMethods.Contains("GET")) return false;
        
        if (chain.RequestType == null && parameter.ParameterType.IsConcrete())
        {
            variable = new ReadJsonBody(parameter).Variable;
            
            // Oh, this does NOT make me feel good
            chain.RequestType = parameter.ParameterType;
            return true;
        }

        return false;
    }
}