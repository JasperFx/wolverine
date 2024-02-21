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

internal class ParsedCollectionQueryStringValue : SyncFrame
{
    private readonly Type _collectionElementType;

    public ParsedCollectionQueryStringValue(ParameterInfo parameter)
    {
        Variable = new QuerystringVariable(parameter.ParameterType, parameter.Name!, this);
        _collectionElementType = GetCollectionElementType(parameter.ParameterType);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var collectionAlias = typeof(List<>).MakeGenericType(_collectionElementType).FullNameInCode();
        var elementAlias = _collectionElementType.FullNameInCode();

        writer.Write($"var {Variable.Usage} = new {collectionAlias}();");

        if (_collectionElementType == typeof(string))
        {
            writer.Write($"{Variable.Usage}.AddRange(httpContext.Request.Query[\"{Variable.Usage}\"]);");
        }
        else
        {
            writer.Write($"BLOCK:foreach (var {Variable.Usage}Value in httpContext.Request.Query[\"{Variable.Usage}\"])");

            if (_collectionElementType.IsEnum)
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse<{elementAlias}>({Variable.Usage}Value, out var {Variable.Usage}ValueParsed))");
            }
            else if (_collectionElementType.IsBoolean())
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Usage}Value, out var {Variable.Usage}ValueParsed))");
            }
            else
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Usage}Value, System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}ValueParsed))");
            }

            writer.Write($"{Variable.Usage}.Add({Variable.Usage}ValueParsed);");
            writer.FinishBlock(); // parsing block

            writer.FinishBlock(); // foreach blobck
        }

        Next?.GenerateCode(method, writer);
    }

    public static bool CanParse(Type argType)
    {
        if (!argType.IsGenericType)
        {
            return false;
        }

        var elementType = GetCollectionElementType(argType);

        var genericListConcreteType = typeof(List<>).MakeGenericType(elementType);
        var genericListInterfaceType = typeof(IList<>).MakeGenericType(elementType);
        var genericReadOnlyListType = typeof(IReadOnlyList<>).MakeGenericType(elementType);
        var genericEnumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);

        var collectionTypeSupported = argType == genericListConcreteType
            || argType == genericListInterfaceType
            || argType == genericReadOnlyListType
            || argType == genericEnumerableType;

        var elementTypeSupported = elementType == typeof(string) || RouteParameterStrategy.CanParse(elementType);

        return collectionTypeSupported && elementTypeSupported;
    }

    private static Type GetCollectionElementType(Type collectionType)
        => collectionType.GetGenericArguments()[0];
}

internal class QueryStringParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        variable = chain.TryFindOrCreateQuerystringValue(parameter);
        return variable != null;
    }
}