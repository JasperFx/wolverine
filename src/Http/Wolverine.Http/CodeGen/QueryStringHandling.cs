using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

public enum AssignMode
{
    WriteToVariable,
    WriteToProperty
}

// TODO -- move this to JasperFx.CodeGeneration later
public interface IGeneratesCode
{
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


internal class ParsedCollectionQueryStringValue : SyncFrame, IReadHttpFrame
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
    
    public void AssignToProperty(string usage)
    {
        Variable.OverrideName(usage);
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;
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
