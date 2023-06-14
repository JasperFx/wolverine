using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;

namespace Wolverine.Http.CodeGen;

internal class ReadStringQueryStringValue : SyncFrame
{
    public ReadStringQueryStringValue(string name)
    {
        Variable = new Variable(typeof(string), name, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"string {Variable.Usage} = httpContext.Request.Query[\"{Variable.Usage}\"].FirstOrDefault();");

        Next?.GenerateCode(method, writer);
    }
}

internal class ParsedQueryStringValue : SyncFrame
{
    public ParsedQueryStringValue(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.ShortNameInCode();
        writer.Write($"{alias} {Variable.Usage} = default;");
        writer.Write($"{alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], out {Variable.Usage});");

        Next?.GenerateCode(method, writer);
    }
}

internal class ParsedNullableQueryStringValue : SyncFrame
{
    private readonly string _alias;

    public ParsedNullableQueryStringValue(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
        _alias = parameter.ParameterType.GetInnerTypeFromNullable().ShortNameInCode();
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_alias}? {Variable.Usage} = null;");
        writer.Write(
            $"if ({_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");

        Next?.GenerateCode(method, writer);
    }
}

internal class QueryStringParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        variable = default!;

        if (parameter.ParameterType == typeof(string))
        {
            variable = new ReadStringQueryStringValue(parameter.Name!).Variable;
            return true;
        }

        if (parameter.ParameterType.IsNullable())
        {
            var inner = parameter.ParameterType.GetInnerTypeFromNullable();
            if (RouteParameterStrategy.CanParse(inner))
            {
                variable = new ParsedNullableQueryStringValue(parameter).Variable;
                return true;
            }
        }

        if (RouteParameterStrategy.CanParse(parameter.ParameterType))
        {
            variable = new ParsedQueryStringValue(parameter).Variable;
            return true;
        }

        return false;
    }
}