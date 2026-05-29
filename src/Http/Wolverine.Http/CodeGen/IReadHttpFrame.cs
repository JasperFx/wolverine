using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;

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
        // F# emit currently covers string binding to a method argument. The parsed/out-var path and
        // property binding (AsParameters) are deferred (GH-2969).
        if (Mode != AssignMode.WriteToVariable)
        {
            throw new NotSupportedException(
                "F# code generation for ReadHttpFrame supports method-argument binding only (not property/AsParameters binding) yet.");
        }

        if (_rawType != typeof(string))
        {
            throw new NotSupportedException(
                $"F# code generation for ReadHttpFrame does not yet support parsed (non-string) binding (was {_rawType.FullNameInCode()}).");
        }

        writer.Write($"{Variable.FSharpAssignmentUsage} = {fsharpRawValueSource()}");

        if (_source == BindingSource.RouteValue && !_isOptional)
        {
            // A missing required route value 404s and aborts. F# has no early return, so the rest of
            // the chain renders inside the else branch.
            writer.Write($"BLOCK:if isNull {Variable.Usage} then");
            writer.Write($"httpContext.Response.{nameof(HttpResponse.StatusCode)} <- 404");
            writer.Write("()");
            writer.FinishBlock();
            writer.Write("BLOCK:else");
            if (Next != null)
            {
                Next.GenerateFSharpCode(method, writer);
            }
            else
            {
                writer.Write("()");
            }

            writer.FinishBlock();
        }
        else
        {
            Next?.GenerateFSharpCode(method, writer);
        }
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