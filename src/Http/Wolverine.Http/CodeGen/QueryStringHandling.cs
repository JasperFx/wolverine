using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

public enum QueryStringAssignMode
{
    WriteToVariable,
    WriteToProperty
}

public interface IReadQueryStringFrame
{
    void AssignToProperty(string usage);
    QueryStringAssignMode Mode { get; }

    void GenerateCode(GeneratedMethod method, ISourceWriter writer);
}

public class QuerystringVariable : Variable
{
    public QuerystringVariable(Type variableType, string usage, Frame? creator) : base(variableType, usage, creator)
    {
        Name = usage;
    }

    public string Name { get; set; }

}

internal class ReadStringQueryStringValue : SyncFrame, IReadQueryStringFrame
{
    public ReadStringQueryStringValue(string name)
    {
        Variable = new QuerystringVariable(typeof(string), name, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Mode == QueryStringAssignMode.WriteToVariable)
        {
            writer.Write($"string {Variable.Usage} = httpContext.Request.Query[\"{Variable.Name}\"].FirstOrDefault();");
        }
        else
        {
            writer.Write($"{Variable.Usage} = httpContext.Request.Query[\"{Variable.Name}\"].FirstOrDefault();");
        }

        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        Variable.OverrideName(usage);
        Mode = QueryStringAssignMode.WriteToProperty;
    }

    public QueryStringAssignMode Mode { get; private set; } = QueryStringAssignMode.WriteToVariable;
}

internal class ParsedQueryStringValue : SyncFrame, IReadQueryStringFrame
{
    private string _property;

    public ParsedQueryStringValue(Type parameterType, string parameterName)
    {
        Variable = new QuerystringVariable(parameterType, parameterName!, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.FullNameInCode();
        var prefix = Mode == QueryStringAssignMode.WriteToVariable ? "" : "if (";
        var suffix = Mode == QueryStringAssignMode.WriteToVariable ? "" : $") {_property} = {Variable.Usage}";
        var outUsage = Mode == QueryStringAssignMode.WriteToVariable ? Variable.Usage : $"var {Variable.Usage}";
        
        if (Mode == QueryStringAssignMode.WriteToVariable)
        {
            writer.Write($"{alias} {Variable.Usage} = default;");
        }

        if (Variable.VariableType.IsEnum)
        {
            writer.Write($"{prefix}{alias}.TryParse<{alias}>(httpContext.Request.Query[\"{Variable.Name}\"], true, out {outUsage}){suffix};");
        }
        else if (Variable.VariableType.IsBoolean())
        {
            writer.Write($"{prefix}{alias}.TryParse(httpContext.Request.Query[\"{Variable.Name}\"], out {outUsage}){suffix};");
        }
        else
        {
            writer.Write($"{prefix}{alias}.TryParse(httpContext.Request.Query[\"{Variable.Name}\"], System.Globalization.CultureInfo.InvariantCulture, out {outUsage}){suffix};");
        }

        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        Mode = QueryStringAssignMode.WriteToProperty;
        _property = usage;
    }

    public QueryStringAssignMode Mode { get; private set; } = QueryStringAssignMode.WriteToVariable;
}

internal class ParsedNullableQueryStringValue : SyncFrame, IReadQueryStringFrame
{
    private readonly string _alias;
    private Type _innerTypeFromNullable;
    private string _property;

    public ParsedNullableQueryStringValue(Type parameterType, string parameterName)
    {
        Variable = new QuerystringVariable(parameterType, parameterName, this);
        _innerTypeFromNullable = parameterType.GetInnerTypeFromNullable();
        _alias = _innerTypeFromNullable.FullNameInCode();
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Mode == QueryStringAssignMode.WriteToVariable)
        {
            writer.Write($"{_alias}? {Variable.Usage} = null;");
            
            if (_innerTypeFromNullable.IsEnum)
            {
                writer.Write(
                    $"if ({_alias}.TryParse<{_innerTypeFromNullable.FullNameInCode()}>(httpContext.Request.Query[\"{Variable.Usage}\"], true, out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
            }
            else if (_innerTypeFromNullable.IsBoolean())
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
            }
            else
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
            }
        }
        else
        {
            if (_innerTypeFromNullable.IsEnum)
            {
                writer.Write(
                    $"if ({_alias}.TryParse<{_innerTypeFromNullable.FullNameInCode()}>(httpContext.Request.Query[\"{Variable.Usage}\"], true, out var {Variable.Usage})) {_property} = {Variable.Usage};");
            }
            else if (_innerTypeFromNullable.IsBoolean())
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], out var {Variable.Usage})) {_property} = {Variable.Usage};");
            }
            else
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Query[\"{Variable.Usage}\"], System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage})) {_property} = {Variable.Usage};");
            }
        }
        

        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = QueryStringAssignMode.WriteToProperty;
    }

    public QueryStringAssignMode Mode { get; private set; } = QueryStringAssignMode.WriteToVariable;
}

internal class ParsedCollectionQueryStringValue : SyncFrame, IReadQueryStringFrame
{
    private readonly Type _collectionElementType;

    public ParsedCollectionQueryStringValue(Type parameterType, string parameterName)
    {
        Variable = new QuerystringVariable(parameterType, parameterName!, this);
        _collectionElementType = GetCollectionElementType(parameterType);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var collectionAlias = typeof(List<>).MakeGenericType(_collectionElementType).FullNameInCode();
        var elementAlias = _collectionElementType.FullNameInCode();

        if (Mode == QueryStringAssignMode.WriteToVariable)
        {
            writer.Write($"var {Variable.Usage} = new {collectionAlias}();");
        }
        else
        {
            writer.Write($"{Variable.Usage} = new {collectionAlias}();");
        }


        if (_collectionElementType == typeof(string))
        {
            writer.Write($"{Variable.Usage}.AddRange(httpContext.Request.Query[\"{Variable.Name}\"]);");
        }
        else
        {
            writer.Write($"BLOCK:foreach (var {Variable.Name}Value in httpContext.Request.Query[\"{Variable.Name}\"])");

            if (_collectionElementType.IsEnum)
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse<{elementAlias}>({Variable.Name}Value, true, out var {Variable.Name}ValueParsed))");
            }
            else if (_collectionElementType.IsBoolean())
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Name}Value, out var {Variable.Name}ValueParsed))");
            }
            else
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Name}Value, System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Name}ValueParsed))");
            }

            writer.Write($"{Variable.Usage}.Add({Variable.Name}ValueParsed);");
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
    
    public void AssignToProperty(string usage)
    {
        Variable.OverrideName(usage);
        Mode = QueryStringAssignMode.WriteToProperty;
    }

    public QueryStringAssignMode Mode { get; private set; } = QueryStringAssignMode.WriteToVariable;
}

internal class QueryStringParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.GetCustomAttribute<FromFormAttribute>() != null)
        {
            variable = null;
            return false;
        }
        variable = chain.TryFindOrCreateQuerystringValue(parameter);
        return variable != null;
    }
}
