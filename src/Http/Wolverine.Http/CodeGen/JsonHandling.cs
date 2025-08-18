using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

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

    public ReadJsonBody(Type requestType)
    {
        Variable = new Variable(requestType, this);
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
    
    public ReadJsonBodyWithNewtonsoft(Type requestType) : base(typeof(NewtonsoftHttpSerialization), findMethodForType(requestType))
    {
        var parameterName = Variable.DefaultArgName(requestType);
        if (parameterName == "_")
        {
            parameterName = Variable.DefaultArgName(requestType);
        }

        ReturnVariable!.OverrideName(parameterName);

        CommentText = "Reading the request body with JSON deserialization";
    }
}

internal class JsonBodyParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        variable = default!;

        if (chain.HttpMethods.Contains("GET"))
        {
            return false;
        }

        if (parameter.HasAttribute<NotBodyAttribute>())
        {
            return false;
        }

        if(parameter.HasAttribute<FromFormAttribute>())
        {
            return false;
        }

        if(parameter.HasAttribute<AsParametersAttribute>()){
            return false;
        }

        if (chain.RequestType == null && parameter.ParameterType.IsConcrete())
        {
            // It *could* be used twice, so let's watch out for this!
            chain.RequestBodyVariable ??= Usage == JsonUsage.SystemTextJson
                ? new ReadJsonBody(parameter).Variable
                : new ReadJsonBodyWithNewtonsoft(parameter).ReturnVariable!;

            variable = chain.RequestBodyVariable;

            // Oh, this does NOT make me feel good!
            chain.RequestType = parameter.ParameterType;
            return true;
        }

        return false;
    }

    public JsonUsage Usage { get; set; } = JsonUsage.SystemTextJson;

    public bool TryBuildVariable(HttpChain chain, out Variable variable)
    {
        if (chain.RequestType.IsConcrete())
        {
            // It *could* be used twice, so let's watch out for this!
            chain.RequestBodyVariable ??= Usage == JsonUsage.SystemTextJson
                ? new ReadJsonBody(chain.RequestType).Variable
                : new ReadJsonBodyWithNewtonsoft(chain.RequestType).ReturnVariable!;

            variable = chain.RequestBodyVariable;

            return true;
        }

        variable = default;
        return false;
    }
}