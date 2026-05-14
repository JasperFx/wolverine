using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.Newtonsoft.CodeGen;

internal class ReadJsonBodyWithNewtonsoft : MethodCall
{
    private static MethodInfo findMethodForType(Type parameterType)
    {
        return typeof(NewtonsoftHttpSerialization).GetMethod(nameof(NewtonsoftHttpSerialization.ReadFromJsonAsync))!
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
