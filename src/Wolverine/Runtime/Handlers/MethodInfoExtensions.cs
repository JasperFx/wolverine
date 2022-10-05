using System;
using System.Linq;
using System.Reflection;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public static class MethodInfoExtensions
{
    /// <summary>
    /// Determine the message type for the handler method, but it's always the first
    /// parameter now:)
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
        return parameters.FirstOrDefault()?.ParameterType;
    }
}
