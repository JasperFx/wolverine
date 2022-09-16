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
        if (!parameters.Any())
        {
            return null;
        }

        if (parameters.Length == 1)
        {
            return parameters.First().ParameterType;
        }

        var matching = parameters.FirstOrDefault(x => x.Name!.IsIn("message", "input", "@event", "command"));

        if (matching != null)
        {
            return matching.ParameterType;
        }

        return parameters.First().ParameterType.IsInputTypeCandidate()
            ? parameters.First().ParameterType
            : null;
    }
}
