using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.CodeGen;

internal class ReadStringRouteValue : SyncFrame
{
    public ReadStringRouteValue(string name)
    {
        Variable = new Variable(typeof(string), name, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = (string)httpContext.GetRouteValue(\"{Variable.Usage}\");");
        Next?.GenerateCode(method, writer);
    }
}

internal class ParsedRouteArgumentFrame : SyncFrame
{
    public ParsedRouteArgumentFrame(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.ShortNameInCode();
        writer.Write(
            $"BLOCK:if (!{alias}.TryParse((string)httpContext.GetRouteValue(\"{Variable.Usage}\"), out var {Variable.Usage}))");
        writer.WriteLine(
            $"httpContext.Response.{nameof(HttpResponse.StatusCode)} = 404;");
        writer.WriteLine(method.ToExitStatement());
        writer.FinishBlock();

        writer.BlankLine();
        Next?.GenerateCode(method, writer);
    }
}

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
        { typeof(Guid), typeof(Guid).FullName },
        { typeof(DateTime), typeof(DateTime).FullName },
        { typeof(DateTimeOffset), typeof(DateTimeOffset).FullName }
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

    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        var matches = chain.RoutePattern.Parameters.Any(x => x.Name == parameter.Name);
        if (matches)
        {
            if (parameter.ParameterType == typeof(string))
            {
                variable = new ReadStringRouteValue(parameter.Name).Variable;
                return true;
            }

            if (CanParse(parameter.ParameterType))
            {
                variable = new ParsedRouteArgumentFrame(parameter).Variable;
                return true;
            }
        }

        variable = null;
        return matches;
    }

    public static bool CanParse(Type argType)
    {
        return TypeOutputs.ContainsKey(argType);
    }
}