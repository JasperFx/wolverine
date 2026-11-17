using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

/// <summary>
///     Reads the request form with <c>ReadFormAsync</c>. <c>Request.Form</c> parses a form that has not been
///     read yet synchronously, blocking the request thread while a large multipart upload arrives.
/// </summary>
internal class ReadFormCollection : AsyncFrame
{
    private Variable? _httpContext;

    public ReadFormCollection()
    {
        Variable = new Variable(typeof(IFormCollection), "formCollection", this);
    }

    public Variable Variable { get; }

    /// <summary>
    ///     Orders the form read after <paramref name="frame" /> wherever the arranger puts it. A form value's
    ///     binding never sits in <see cref="IChain.Middleware" />, so this frame is usually reached only through
    ///     <see cref="FormCollectionSource" />, and JasperFx's <c>MethodFrameArranger</c> inserts a frame it
    ///     reaches that way at the very front of the method, ahead of the antiforgery check. ASP.NET Core
    ///     refuses to read the form of a request that failed antiforgery validation and throws instead (a 500),
    ///     so the read has to follow the check that answers 400. It has to follow the request buffering of a
    ///     <c>[DeduplicatedWithResponse]</c> endpoint too, or the fingerprint has no body left to read.
    /// </summary>
    internal void MustFollow(Frame frame)
    {
        dependencies.Fill(frame);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Read the form without blocking the request thread");
        writer.Write(
            $"var {Variable.Usage} = await {_httpContext!.Usage}.Request.ReadFormAsync({_httpContext.Usage}.RequestAborted).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Read the form without blocking the request thread");
        writer.Write(
            $"let! {Variable.Usage} = {_httpContext!.FSharpUsage}.Request.ReadFormAsync({_httpContext.FSharpUsage}.RequestAborted)");
        Next?.GenerateFSharpCode(method, writer);
    }
}

/// <summary>
///     Resolves <see cref="IFormCollection" /> to the chain's one <see cref="ReadFormCollection" />, so form
///     values, files and an <see cref="IFormCollection" /> parameter all share a single read.
/// </summary>
internal class FormCollectionSource : IVariableSource
{
    private readonly HttpChain _chain;

    public FormCollectionSource(HttpChain chain)
    {
        _chain = chain;
    }

    public bool Matches(Type type) => type == typeof(IFormCollection);

    public Variable Create(Type type) => _chain.ReadFormCollection.Variable;
}

internal class ParsedCollectionFormValue : SyncFrame, IReadHttpFrame
{
    private readonly Type _collectionElementType;
    private Variable? _form;

    public ParsedCollectionFormValue(Type parameterType, string parameterName)
    {
        Variable = new HttpElementVariable(parameterType, parameterName!, this);
        _collectionElementType = GetCollectionElementType(parameterType);
    }

    public HttpElementVariable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _form = chain.FindVariable(typeof(IFormCollection));
        yield return _form;
    }

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
            writer.Write($"{Variable.Usage}.AddRange({_form!.Usage}[\"{Variable.Name}\"]);");
        }
        else
        {
            writer.Write($"BLOCK:foreach (var {Variable.Name}Value in {_form!.Usage}[\"{Variable.Name}\"])");

            // Enums parse case-insensitively, matching the scalar binder
            writer.Write($"BLOCK:if ({ParsingCode.TryParse(_collectionElementType!, $"{Variable.Name}Value", $"var {Variable.Name}ValueParsed")})");

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
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;
}


internal class ParsedArrayFormValue : SyncFrame, IReadHttpFrame
{
    private string? _property;
    private Variable? _form;

    public ParsedArrayFormValue(Type parameterType, string parameterName) 
    {
        Variable = new HttpElementVariable(parameterType, parameterName!, this);
    }

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;

    public HttpElementVariable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _form = chain.FindVariable(typeof(IFormCollection));
        yield return _form;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var elementType = Variable.VariableType.GetElementType();
        if (elementType == typeof(string))
        {
            if (Mode == AssignMode.WriteToVariable)
            {
                writer.Write($"var {Variable.Usage} = {_form!.Usage}[\"{Variable.Usage}\"].ToArray();");
            }
            else
            {
                writer.Write($"{_property} = {_form!.Usage}[\"{Variable.Usage}\"].ToArray();");
            }
        }
        else
        {
            var collectionAlias = typeof(List<>).MakeGenericType(elementType!).FullNameInCode();
            var elementAlias = elementType!.FullNameInCode();

            writer.Write($"var {Variable.Usage}_List = new {collectionAlias}();");
            
            writer.Write($"BLOCK:foreach (var {Variable.Usage}Value in {_form!.Usage}[\"{Variable.Usage}\"])");

            // Enums parse case-insensitively, matching the scalar binder
            writer.Write($"BLOCK:if ({ParsingCode.TryParse(elementType!, $"{Variable.Usage}Value", $"var {Variable.Usage}ValueParsed")})");

            writer.Write($"{Variable.Usage}_List.Add({Variable.Usage}ValueParsed);");
            writer.FinishBlock(); // parsing block

            writer.FinishBlock(); // foreach blobck
            
            if (Mode == AssignMode.WriteToVariable)
            {
                writer.Write($"var {Variable.Usage} = {Variable.Usage}_List.ToArray();");
            }
            else
            {
                writer.Write($"if ({Variable.Usage}_List.Any()) {_property} = {Variable.Usage}_List.ToArray();");
            }
        }
        
        Next?.GenerateCode(method, writer);
    }
}

internal class FormParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if(!parameter.HasAttribute<FromFormAttribute>())
        {
            variable = default;
            return false;
        }
        variable = chain.TryFindOrCreateFormValue(parameter);
        return variable != null;
    }
}
