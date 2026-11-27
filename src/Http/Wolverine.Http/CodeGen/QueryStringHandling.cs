using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Configuration;
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
    internal const string FSharpStrictPropertyBindingNotSupported =
        "F# code generation does not yet support strict query string binding (WolverineHttpOptions.RejectUnparseableQueryValues) of a collection bound to a property of a [FromQuery] or [AsParameters] type. Bind the collection as an endpoint argument, or set RejectUnparseableQueryValues to false.";

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

    /// <summary>
    /// The F# form of a repeated query string value parsed into a <c>List&lt;T&gt;</c>, shared by
    /// <see cref="ParsedArrayQueryStringValue" /> and <see cref="ParsedCollectionQueryStringValue" />. Emits the list,
    /// and in strict mode a second list of the present but unparseable values: F# has no early return out of the
    /// loop, so the rejection is decided after it (see <see cref="WriteFSharpRejectionGuard" />). The C# form stops at
    /// the first unparseable value and rejects with it; the F# form rejects with the same, first, value.
    /// </summary>
    public static void WriteFSharpParsedValues(ISourceWriter writer, string key, Type elementType, string listUsage,
        string valueUsage, string? rejectedUsage)
    {
        writer.Write($"let {listUsage} = System.Collections.Generic.List<{elementType.FSharpName()}>()");
        if (rejectedUsage != null)
        {
            writer.Write($"let {rejectedUsage} = System.Collections.Generic.List<string>()");
        }

        // A property's name is capitalized, and an uppercase identifier in a pattern is warning FS0049
        valueUsage = char.ToLowerInvariant(valueUsage[0]) + valueUsage[1..];

        writer.Write($"BLOCK:for {valueUsage} in httpContext.Request.Query.[\"{key}\"] do");

        // Enums parse case-insensitively, matching the scalar binder
        writer.Write($"BLOCK:match {ParsingCode.FSharpTryParse(elementType, valueUsage)} with");
        writer.Write($"| true, {valueUsage}Parsed -> {listUsage}.Add({valueUsage}Parsed)");
        writer.Write(rejectedUsage == null
            ? "| _ -> ()"
            : $"| _ -> if not (System.String.IsNullOrEmpty({valueUsage})) then {rejectedUsage}.Add({valueUsage})");
        writer.FinishBlock(); // match
        writer.FinishBlock(); // for
    }

    /// <summary>
    /// The F# form of the strict rejection: answers 400 with the first unparseable value, or else runs
    /// <paramref name="writeRest" /> (the binding and the rest of the chain), since F# has no early return.
    /// </summary>
    public static void WriteFSharpRejectionGuard(GeneratedMethod method, ISourceWriter writer, string key,
        string rejectedUsage, Type elementType, Action writeRest)
    {
        var problem =
            $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.WriteQueryValueParsingProblem)}(httpContext, \"{key}\", {rejectedUsage}.[0], \"{elementType.FullNameInCode()}\")";

        writer.Write($"BLOCK:if {rejectedUsage}.Count > 0 then");

        // Inside a task { } body the write is awaited. When the body is instead a bare trailing Task expression
        // (AsyncMode.ReturnFromLastNode), every branch must itself be a Task, and this one is the write's
        writer.Write(method.AsyncMode == AsyncMode.AsyncTask ? $"do! {problem}" : problem);
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        writeRest();
        writer.FinishBlock();
    }

    /// <summary>
    /// The rest of the chain after a reading frame, or the no-op that ends an F# branch when there is none
    /// </summary>
    public static void WriteFSharpNextOrAbort(Frame? next, GeneratedMethod method, ISourceWriter writer)
    {
        if (next != null)
        {
            next.GenerateFSharpCode(method, writer);
        }
        else
        {
            writer.Write(FSharpEmitHelpers.AbortExpression(method));
        }
    }
}

internal class ParsedCollectionQueryStringValue : SyncFrame, IReadHttpFrame
{
    private readonly Type _collectionElementType;
    private readonly bool _rejectUnparseableValue;

    public ParsedCollectionQueryStringValue(Type parameterType, string parameterName,
        bool rejectUnparseableValue = false)
    {
        // The query string key can hold characters like '-' or '.' that are not legal in a C# identifier
        Variable = new HttpElementVariable(parameterType, parameterName!.SanitizeFormNameForVariable(), this);
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
            var value = $"{Variable.Name.SanitizeFormNameForVariable()}Value";
            writer.Write($"BLOCK:foreach (var {value} in httpContext.Request.Query[\"{Variable.Name}\"])");

            // Enums parse case-insensitively, matching the scalar binder
            writer.Write($"BLOCK:if ({ParsingCode.TryParse(_collectionElementType!, value, $"var {value}Parsed")})");

            writer.Write($"{Variable.Usage}.Add({value}Parsed);");
            writer.FinishBlock(); // parsing block

            if (_rejectUnparseableValue)
            {
                StrictQueryBinding.WriteRejectionBlock(method, writer, Variable.Name, value, elementAlias);
            }

            writer.FinishBlock(); // foreach blobck
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // In property mode Variable.Usage is the property ("query.Ids"), so the list is built in a local first
        var listUsage = Mode == AssignMode.WriteToVariable
            ? Variable.Usage
            : $"{Variable.Name.SanitizeFormNameForVariable()}_List";

        if (_collectionElementType == typeof(string))
        {
            writer.Write(
                $"let {listUsage} = System.Collections.Generic.List<string>(httpContext.Request.Query.[\"{Variable.Name}\"])");
            writeFSharpPropertyAssignment(writer, listUsage);
            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        if (_rejectUnparseableValue && Mode == AssignMode.WriteToProperty)
        {
            throw new NotSupportedException(StrictQueryBinding.FSharpStrictPropertyBindingNotSupported);
        }

        var value = $"{Variable.Name.SanitizeFormNameForVariable()}Value";
        var rejected = _rejectUnparseableValue ? $"{value}_Unparseable" : null;

        StrictQueryBinding.WriteFSharpParsedValues(writer, Variable.Name, _collectionElementType, listUsage, value,
            rejected);

        if (rejected == null)
        {
            writeFSharpPropertyAssignment(writer, listUsage);
            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        StrictQueryBinding.WriteFSharpRejectionGuard(method, writer, Variable.Name, rejected, _collectionElementType,
            () => StrictQueryBinding.WriteFSharpNextOrAbort(Next, method, writer));
    }

    private void writeFSharpPropertyAssignment(ISourceWriter writer, string listUsage)
    {
        if (Mode == AssignMode.WriteToProperty)
        {
            writer.Write($"{Variable.Usage} <- {listUsage}");
        }
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
