using System.Reflection;
using JasperFx;
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

/// <summary>
/// Shared emission for the GH-3372 / GH-3398 opt-in strict query string binding. A query string
/// value that is present but cannot be parsed short circuits the request with a 400 ProblemDetails
/// naming the offending parameter, matching ASP.NET Core minimal API binding.
/// </summary>
internal static class StrictQueryBinding
{
    /// <summary>
    /// Emits the "else" arm of a collection element's TryParse: a present but unparseable element
    /// rejects the entire binding. All-or-nothing is deliberate -- keeping the parseable elements
    /// and dropping the bad one would still hand the endpoint a silently wrong filter (GH-3398).
    /// </summary>
    public static void WriteRejectionBlock(GeneratedMethod method, ISourceWriter writer, string parameterName,
        string valueUsage, string elementAlias)
    {
        writer.Write($"BLOCK:else if (!string.IsNullOrEmpty({valueUsage}))");
        writer.Write(
            $"await {nameof(HttpHandler.WriteQueryValueParsingProblem)}(httpContext, \"{parameterName}\", {valueUsage}, \"{elementAlias}\").ConfigureAwait(false);");
        writer.Write(method.ToExitStatement());
        writer.FinishBlock();
    }
}

internal class ParsedCollectionQueryStringValue : SyncFrame, IReadHttpFrame
{
    private readonly Type _collectionElementType;
    private readonly bool _rejectUnparseableValue;

    public ParsedCollectionQueryStringValue(Type parameterType, string parameterName,
        bool rejectUnparseableValue = false)
    {
        Variable = new HttpElementVariable(parameterType, parameterName!, this);
        _collectionElementType = GetCollectionElementType(parameterType);

        // GH-3398 strict query string binding for collections: only meaningful for parsed
        // (non-string) elements. The 400 + ProblemDetails short circuit writes the response
        // asynchronously, so the frame has to force the generated method into async mode.
        _rejectUnparseableValue = rejectUnparseableValue && _collectionElementType != typeof(string);
        if (_rejectUnparseableValue)
        {
            IsAsync = true;
        }
    }

    public HttpElementVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var collectionAlias = typeof(List<>).MakeGenericType(_collectionElementType).FullNameInCode();
        var elementAlias = _collectionElementType.FullNameInCode();

        if (Mode == AssignMode.WriteToVariable)
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

            if (_rejectUnparseableValue)
            {
                StrictQueryBinding.WriteRejectionBlock(method, writer, Variable.Name, $"{Variable.Name}Value",
                    elementAlias);
            }

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
