using System;
using System.Linq;
using System.Reflection;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public static class MethodInfoExtensions
{
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
