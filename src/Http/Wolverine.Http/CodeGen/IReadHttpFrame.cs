using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Configuration;

namespace Wolverine.Http.CodeGen;

public interface IReadHttpFrame : IGeneratesCode
{
    void AssignToProperty(string usage);
    AssignMode Mode { get; }
}

internal enum BindingSource
{
    QueryString,
    Form,
    RouteValue,
    Header
}

internal class ReadHttpFrame : SyncFrame, IReadHttpFrame
{
    private readonly BindingSource _source;

    public ReadHttpFrame(BindingSource source, Type parameterType, string parameterName, bool isOptional = false)
    {
        _source = source;
        Variable = new HttpElementVariable(parameterType, parameterName!.SanitizeFormNameForVariable(), this);

        _isOptional = isOptional;
        _isNullable = parameterType.IsNullable();
        _rawType = _isNullable ? parameterType.GetInnerTypeFromNullable() : parameterType;
    }

    public HttpElementVariable Variable { get; }

    /// <summary>
    /// The query string key, route argument name, or form element name. Mirrors Variable.Name
    /// </summary>
    public string Key
    {
        get => Variable.Name;
        set => Variable.Name = value;
    }

    // string array out of query string will be "special"
    private string rawValueSource()
    {
        switch (_source)
        {
            case BindingSource.Form:
                return $"httpContext.Request.Form[\"{Key}\"]";
            
            case BindingSource.Header:
                return $"{nameof(HttpHandler.ReadSingleHeaderValue)}(httpContext, \"{Key}\")";
            
            case BindingSource.QueryString:
                // TODO -- watch for arrays!!!
                return $"httpContext.Request.Query[\"{Key}\"]";
            
            case BindingSource.RouteValue:
                return $"(string?)httpContext.GetRouteValue(\"{Key}\")";
            
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    // type.TryParse(raw value, out {assignedExpression})
    private string tryParseExpression(string assignedExpression)
    {
        var typeName = _rawType.FullNameInCode();
        var rawValue = $"{Variable.Usage}_rawValue";

        if (_rawType.IsEnum)
        {
            return $"{typeName}.TryParse<{_rawType.FullNameInCode()}>({rawValue}, true, out {assignedExpression})";
        }

        if (_rawType.IsBoolean())
        {
            return $"{typeName}.TryParse({rawValue}, out {assignedExpression})";
        }
        
        return $"{rawValue} != null && {typeName}.TryParse({rawValue}, System.Globalization.CultureInfo.InvariantCulture, out {assignedExpression})";
    }
    
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_rawType == typeof(string))
        {
            writeStringValue(writer, method);
        }
        else
        {
            writeParsedValue(writer, method);
        }

        // TODO -- going to do special cases for query string arrays, maybe form arrays?



        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# emit covers binding to a method argument. Property binding (AsParameters) is deferred.
        if (Mode != AssignMode.WriteToVariable)
        {
            throw new NotSupportedException(
                "F# code generation for ReadHttpFrame supports method-argument binding only (not property/AsParameters binding) yet.");
        }

        if (_rawType == typeof(string))
        {
            writeStringValueFSharp(method, writer);
        }
        else
        {
            writeParsedValueFSharp(method, writer);
        }
    }

    private void writeStringValueFSharp(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{Variable.FSharpAssignmentUsage} = {fsharpRawValueSource()}");

        if (_source == BindingSource.RouteValue && !_isOptional)
        {
            // A missing required route value 404s and aborts. F# has no early return, so the rest of
            // the chain renders inside the else branch.
            writer.Write($"BLOCK:if isNull {Variable.Usage} then");
            writeStatusAbort(method, writer);
            writer.FinishBlock();
            writer.Write("BLOCK:else");
            WriteNextOrUnit(method, writer);
            writer.FinishBlock();
        }
        else
        {
            Next?.GenerateFSharpCode(method, writer);
        }
    }

    private void writeParsedValueFSharp(GeneratedMethod method, ISourceWriter writer)
    {
        if (_isNullable || _isOptional)
        {
            throw new NotSupportedException(
                $"F# code generation does not yet support nullable/optional parsed binding ({_rawType.FullNameInCode()}).");
        }

        if (_rawType.IsEnum)
        {
            throw new NotSupportedException(
                $"F# code generation does not yet support enum binding ({_rawType.FullNameInCode()}).");
        }

        var raw = $"{Variable.Usage}_rawValue";
        writer.Write($"let {raw} = {fsharpRawValueSource()}");

        if (_source == BindingSource.RouteValue)
        {
            // Required route value: null or parse-failure 404s + aborts; success binds the variable and
            // renders the rest of the chain in the success match arm (no F# early return).
            writer.Write($"BLOCK:if isNull {raw} then");
            writeStatusAbort(method, writer);
            writer.FinishBlock();
            writer.Write("BLOCK:else");
            writer.Write($"BLOCK:match {fsharpTryParse(raw)} with");
            writer.Write($"BLOCK:| true, {Variable.Usage} ->");
            WriteNextOrUnit(method, writer);
            writer.FinishBlock();
            writer.Write("BLOCK:| _ ->");
            writeStatusAbort(method, writer);
            writer.FinishBlock();
            writer.FinishBlock(); // match
            writer.FinishBlock(); // else
        }
        else
        {
            // Query/form/header: bind the parsed value, or the default when parsing fails, then continue.
            writer.Write(
                $"{Variable.FSharpAssignmentUsage} = match {fsharpTryParse(raw)} with | true, v -> v | _ -> Unchecked.defaultof<{_rawType.FSharpName()}>");
            Next?.GenerateFSharpCode(method, writer);
        }
    }

    private void WriteNextOrUnit(GeneratedMethod method, ISourceWriter writer)
    {
        if (Next != null)
        {
            Next.GenerateFSharpCode(method, writer);
        }
        else
        {
            writer.Write(FSharpEmitHelpers.AbortExpression(method));
        }
    }

    private static void writeStatusAbort(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"httpContext.Response.{nameof(HttpResponse.StatusCode)} <- 404");
        writer.Write(FSharpEmitHelpers.AbortExpression(method));
    }

    // The F# tuple form of the C# out-parameter TryParse (F# auto-tuples the out arg).
    private string fsharpTryParse(string raw)
    {
        if (_rawType.IsBoolean())
        {
            return $"System.Boolean.TryParse({raw})";
        }

        return $"{_rawType.FullName}.TryParse({raw}, System.Globalization.CultureInfo.InvariantCulture)";
    }

    private string fsharpRawValueSource()
    {
        switch (_source)
        {
            case BindingSource.Form:
                return $"httpContext.Request.Form.[\"{Key}\"].ToString()";

            case BindingSource.Header:
                return $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.ReadSingleHeaderValue)}(httpContext, \"{Key}\")";

            case BindingSource.QueryString:
                return $"httpContext.Request.Query.[\"{Key}\"].ToString()";

            case BindingSource.RouteValue:
                return $"(httpContext.GetRouteValue(\"{Key}\") :?> string)";

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void writeStringValue(ISourceWriter writer, GeneratedMethod method)
    {
        var assignTo = Mode == AssignMode.WriteToVariable ? $"var {Variable.Usage}" : _property;
        
        writer.Write($"{assignTo} = {rawValueSource()};");
        
        if (_source == BindingSource.RouteValue)
        {
            var check = Mode == AssignMode.WriteToVariable ? Variable.Usage : _property;

            if (!_isOptional)
            {
                writer.Write($"BLOCK:if({check} == null)");
                writer.WriteLine(
                    $"httpContext.Response.{nameof(HttpResponse.StatusCode)} = 404;");
                writer.WriteLine(method.ToExitStatement());
                writer.FinishBlock();
            }
        }
    }

    private void writeParsedValue(ISourceWriter writer, GeneratedMethod method)
    {
        writer.Write($"string {Variable.Usage}_rawValue = {rawValueSource()};");
        
        if (Mode == AssignMode.WriteToVariable)
        {
            writer.Write($"{Variable.VariableType.FullNameInCode()} {Variable.Usage} = default;");
        }
        
        var assignmentLine = Mode == AssignMode.WriteToVariable ? "" : $"{_property} = {Variable.Usage};";
        var outUsage = Mode == AssignMode.WriteToVariable ? Variable.Usage : $"var {Variable.Usage}";
        
        if (_isNullable)
        {
            outUsage = $"var {Variable.Usage}_parsed";

            var assignTo = Mode == AssignMode.WriteToVariable ? Variable.Usage : _property;
            assignmentLine = $"{assignTo} = {Variable.Usage}_parsed;";
        }

        writer.Write("");
        writer.Write($"BLOCK:if ({tryParseExpression(outUsage)})");
        writer.Write(assignmentLine);
        
        writer.FinishBlock();

        if (_source == BindingSource.RouteValue)
        {
            if (_isNullable || _isOptional)
            {
                writer.Write($"BLOCK:else if (!string.IsNullOrWhiteSpace({Variable.Usage}_rawValue))");
            }
            else
            {
                writer.WriteElse();
            }

            writer.WriteLine(
                $"httpContext.Response.{nameof(HttpResponse.StatusCode)} = 404;");
            writer.WriteLine(method.ToExitStatement());
            writer.FinishBlock();
        }
    }


    private string? _property;
    private readonly bool _isOptional;
    private readonly bool _isNullable;
    private readonly Type _rawType;

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;
}