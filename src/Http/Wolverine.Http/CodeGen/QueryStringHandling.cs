using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.CodeGen;

public class QuerystringVariable : Variable
{
    public QuerystringVariable(Type variableType, string usage, Frame? creator) : base(variableType, usage, creator)
    {
        Name = usage;
    }

    public string Name { get; set; }
}

internal class ReadStringQueryStringValue : SyncFrame
{
    public ReadStringQueryStringValue(string name)
    {
        Variable = new QuerystringVariable(typeof(string), name, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"string {Variable.Usage} = httpContext.Request.Query[\"{Variable.Name}\"].FirstOrDefault();");

        Next?.GenerateCode(method, writer);
    }
}

internal class ParsedQueryStringValue : SyncFrame
{
    public ParsedQueryStringValue(ParameterInfo parameter)
    {
        Variable = new QuerystringVariable(parameter.ParameterType, parameter.Name!, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.FullNameInCode();
        writer.Write($"{alias} {Variable.Usage} = default;");


        if (Variable.VariableType.IsEnum)
        {
            writer.Write($"{alias}.TryParse<{alias}>(httpContext.Request.Query[\"{Variable.Name}\"], out {Variable.Usage});");
        }
        else if (Variable.VariableType.IsBoolean())
        {
            writer.Write($"{alias}.TryParse(httpContext.Request.Query[\"{Variable.Name}\"], out {Variable.Usage});");
        }
        else
        {
            writer.Write($"{alias}.TryParse(httpContext.Request.Query[\"{Variable.Name}\"], System.Globalization.CultureInfo.InvariantCulture, out {Variable.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }
}

internal class ParsedNullableQueryStringValue : SyncFrame
{
    private readonly string _alias;
    private Type _innerTypeFromNullable;

    public ParsedNullableQueryStringValue(ParameterInfo parameter)
    {
        Variable = new QuerystringVariable(parameter.ParameterType, parameter.Name!, this);
        _innerTypeFromNullable = parameter.ParameterType.GetInnerTypeFromNullable();
        _alias = _innerTypeFromNullable.FullNameInCode();
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_alias}? {Variable.Usage} = null;");
        if (_innerTypeFromNullable.IsEnum)
        {
            writer.Write(
                $"if ({_alias}.TryParse<{_innerTypeFromNullable.FullNameInCode()}>(httpContext.Request.Query[\"{Variable.Usage}\"], out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
        }
        else if (_innerTypeFromNullable.IsBoolean())
        {
            writer.Write($"{_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], out {Variable.Usage});");
        }
        else
        {
            writer.Write(
                $"if ({_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
        }

        Next?.GenerateCode(method, writer);
    }
}

internal class QueryStringParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        variable = chain.TryFindOrCreateQuerystringValue(parameter);
        return variable != null;
    }
}