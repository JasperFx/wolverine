using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class RouteParameterStrategy : IParameterStrategy
{
    #region sample_supported_route_parameter_types

    public static readonly Dictionary<Type, string> TypeOutputs = new()
    {
        { typeof(bool), "bool" },
        { typeof(byte), "byte" },
        { typeof(sbyte), "sbyte" },
        { typeof(char), "char" },
        { typeof(decimal), "decimal" },
        { typeof(float), "float" },
        { typeof(short), "short" },
        { typeof(int), "int" },
        { typeof(double), "double" },
        { typeof(long), "long" },
        { typeof(ushort), "ushort" },
        { typeof(uint), "uint" },
        { typeof(ulong), "ulong" },
        { typeof(Guid), typeof(Guid).FullName! },
        { typeof(DateTime), typeof(DateTime).FullName! },
        { typeof(DateTimeOffset), typeof(DateTimeOffset).FullName! },
        { typeof(DateOnly), typeof(DateOnly).FullName! }
    };

    #endregion

    public static readonly Dictionary<Type, string> TypeRouteConstraints = new()
    {
        { typeof(bool), "bool" },
        { typeof(string), "alpha" },
        { typeof(decimal), "decimal" },
        { typeof(float), "float" },
        { typeof(int), "int" },
        { typeof(double), "double" },
        { typeof(long), "long" },
        { typeof(Guid), "guid" },
        { typeof(DateTime), "datetime" }
    };

    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        return chain.FindRouteVariable(parameter, out variable);
    }

    public static bool CanParse(Type argType)
    {
        return TypeOutputs.ContainsKey(argType) || argType.IsEnum;
    }

    public static void TryApplyRouteVariables(HttpChain chain, MethodCall call)
    {
        for (int i = 0; i < call.Arguments.Length; i++)
        {
            // Don't override them at all of course
            if (call.Arguments[i] == null)
            {
                var parameter = call.Method.GetParameters()[i];
                if (parameter.ParameterType == typeof(string) || CanParse(parameter.ParameterType))
                {
                    if (chain.FindRouteVariable(parameter.ParameterType, parameter.Name, out var variable))
                    {
                        call.Arguments[i] = variable;
                    }
                }
            }
        }
    }
}