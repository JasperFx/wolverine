using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;

namespace Wolverine.Http.CodeGen;

internal class ReadJsonBody : AsyncFrame
{
    public ReadJsonBody(ParameterInfo parameter)
    {
        var parameterName = parameter.Name!;
        if (parameterName == "_")
        {
            parameterName = Variable.DefaultArgName(parameter.ParameterType);
        }
        
        Variable = new Variable(parameter.ParameterType, parameterName, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Reading the request body via JSON deserialization");
        writer.Write(
            $"var ({Variable.Usage}, jsonContinue) = await ReadJsonAsync<{Variable.VariableType.FullNameInCode()}>(httpContext);");
        writer.Write(
            $"if (jsonContinue == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)}) return;");

        Next?.GenerateCode(method, writer);
    }
}

internal class ReadJsonBodyWithNewtonsoft : MethodCall
{
    private static MethodInfo findMethodForType(Type parameterType)
    {
        return typeof(NewtonsoftHttpSerialization).GetMethod(nameof(NewtonsoftHttpSerialization.ReadFromJsonAsync))
            .MakeGenericMethod(parameterType);
    }
    
    public ReadJsonBodyWithNewtonsoft(ParameterInfo parameter) : base(typeof(NewtonsoftHttpSerialization), findMethodForType(parameter.ParameterType))
    {
        var parameterName = parameter.Name!;
        if (parameterName == "_")
        {
            parameterName = Variable.DefaultArgName(parameter.ParameterType);
        }
        
        ReturnVariable!.OverrideName(parameterName);

        CommentText = "Reading the request body with JSON deserialization";
    }
}

internal class JsonBodyParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        variable = default!;

        if (chain.HttpMethods.Contains("GET"))
        {
            return false;
        }

        if (parameter.HasAttribute<NotBodyAttribute>()) return false;

        if (chain.RequestType == null && parameter.ParameterType.IsConcrete())
        {
            variable = Usage == JsonUsage.SystemTextJson 
                ? new ReadJsonBody(parameter).Variable 
                : new ReadJsonBodyWithNewtonsoft(parameter).ReturnVariable!;
            
            // Oh, this does NOT make me feel good
            chain.RequestType = parameter.ParameterType;
            return true;
        }

        return false;
    }

    public JsonUsage Usage { get; set; } = JsonUsage.SystemTextJson;
}