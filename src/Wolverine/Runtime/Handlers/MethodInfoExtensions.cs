using System.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Runtime.Handlers;

public static class MethodInfoExtensions
{
    /// <summary>
    ///     Determine the message type for the handler method, but it's always the first
    ///     parameter now:)
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static Type? MessageType(this MethodInfo method)
    {
        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        var parameters = method.GetParameters();
        var paramType = parameters.FirstOrDefault()?.ParameterType;

        if (paramType is { IsGenericType: true })
        {
            var genericDef = paramType.GetGenericTypeDefinition();
            if (genericDef.GetCustomAttribute<WolverineMessageWrapperAttribute>() != null)
            {
                return paramType.GetGenericArguments()[0];
            }
        }

        return paramType;
    }
}