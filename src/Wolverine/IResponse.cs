using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine;

// TODO -- might have to take off the IWolverineReturnType when we get to http
public interface IResponse : IWolverineReturnType
{
    // Method name will be Build or BuildAsync
}

// It's only a handler policy because HTTP will need to deal
// with it a little bit differently
internal class ResponsePolicy : IHandlerPolicy
{
    public const string SyncMethod = "Build";
    public const string AsyncMethod = "BuildAsync";
    
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var responses = chain.As<IChain>().ReturnVariablesOfType<IResponse>();
            foreach (var response in responses)
            {
                var method = findMethod(response.VariableType);
                if (method == null)
                {
                    throw new InvalidCustomResponseException(
                        $"Invalid Wolverine response exception for {response.VariableType.FullNameInCode()}, no public {SyncMethod}/{AsyncMethod} method found");
                }

                foreach (var parameter in method.GetParameters()) chain.AddDependencyType(parameter.ParameterType);

                response.UseReturnAction(_ =>
                {
                    var buildResponse = new MethodCall(response.VariableType, method)
                    {
                        Target = response,
                        CommentText = $"Placed by Wolverine's {nameof(IResponse)} policy"
                    };
                    
                    buildResponse.ReturnVariable.OverrideName("response_of_" + buildResponse.ReturnVariable.Usage);

                    var captureAsCascading = new CaptureCascadingMessages(buildResponse.ReturnVariable);
                    
                    return new IfElseNullGuardFrame.IfNullGuardFrame(
                        response,
                        buildResponse, captureAsCascading);
                }, "Custom Response Policy");
            }
        }
    }

    private MethodInfo findMethod(Type responseType)
    {
        return
            responseType.GetMethod(SyncMethod,
                BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance)
            ?? responseType.GetMethod(AsyncMethod,
                BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance)
            ?? responseType.GetInterfaces().FirstValue(findMethod);
    }
}

public class InvalidCustomResponseException : Exception
{
    public InvalidCustomResponseException(string? message) : base(message)
    {
    }
}

public interface IResponseAware : IWolverineReturnType
{
    static abstract void ConfigureResponse(IChain chain);
}