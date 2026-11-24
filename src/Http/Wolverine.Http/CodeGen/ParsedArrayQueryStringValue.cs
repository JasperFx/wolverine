using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.CodeGen;

internal class ParsedArrayQueryStringValue : SyncFrame, IReadHttpFrame
{
    private string? _property;
    private readonly bool _rejectUnparseableValue;

    public ParsedArrayQueryStringValue(Type parameterType, string parameterName, bool rejectUnparseableValue = false)
    {
        // The query string key can hold characters like '-' or '.' that are not legal in a C# identifier
        Variable = new HttpElementVariable(parameterType, parameterName!.SanitizeFormNameForVariable(), this);

        // GH-3398 strict query string binding for collections: only meaningful for parsed
        // (non-string) elements. The 400 + ProblemDetails short circuit writes the response
        // asynchronously, so the frame has to force the generated method into async mode.
        _rejectUnparseableValue = rejectUnparseableValue && parameterType.GetElementType() != typeof(string);
        if (_rejectUnparseableValue)
        {
            IsAsync = true;
        }
    }

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;

    public HttpElementVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var elementType = Variable.VariableType.GetElementType();
        if (elementType == typeof(string))
        {
            if (Mode == AssignMode.WriteToVariable)
            {
                writer.Write($"var {Variable.Usage} = httpContext.Request.Query[\"{Variable.Name}\"].ToArray();");
            }
            else
            {
                writer.Write($"{_property} = httpContext.Request.Query[\"{Variable.Name}\"].ToArray();");
            }
        }
        else
        {
            var collectionAlias = typeof(List<>).MakeGenericType(elementType!).FullNameInCode();
            var elementAlias = elementType!.FullNameInCode();

            writer.Write($"var {Variable.Usage}_List = new {collectionAlias}();");
            
            writer.Write($"BLOCK:foreach (var {Variable.Usage}Value in httpContext.Request.Query[\"{Variable.Name}\"])");

            // Enum names parse case-insensitively, matching the scalar binder
            writer.Write($"BLOCK:if ({ParsingCode.TryParse(elementType!, $"{Variable.Usage}Value", $"var {Variable.Usage}ValueParsed")})");

            writer.Write($"{Variable.Usage}_List.Add({Variable.Usage}ValueParsed);");
            writer.FinishBlock(); // parsing block

            writeRejectionBlock(method, writer, $"{Variable.Usage}Value", elementAlias);

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

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var elementType = Variable.VariableType.GetElementType()!;
        if (elementType == typeof(string))
        {
            writer.Write(Mode == AssignMode.WriteToVariable
                ? $"let {Variable.Usage} = httpContext.Request.Query.[\"{Variable.Name}\"].ToArray()"
                : $"{_property} <- httpContext.Request.Query.[\"{Variable.Name}\"].ToArray()");

            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        if (_rejectUnparseableValue && Mode == AssignMode.WriteToProperty)
        {
            throw new NotSupportedException(StrictQueryBinding.FSharpStrictPropertyBindingNotSupported);
        }

        var list = $"{Variable.Usage}_List";
        var value = $"{Variable.Usage}Value";
        var rejected = _rejectUnparseableValue ? $"{Variable.Usage}_Unparseable" : null;

        StrictQueryBinding.WriteFSharpParsedValues(writer, Variable.Name, elementType, list, value, rejected);

        if (rejected == null)
        {
            writeFSharpAssignment(writer, list);
            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        StrictQueryBinding.WriteFSharpRejectionGuard(method, writer, Variable.Name, rejected, elementType, () =>
        {
            writeFSharpAssignment(writer, list);
            StrictQueryBinding.WriteFSharpNextOrAbort(Next, method, writer);
        });
    }

    private void writeFSharpAssignment(ISourceWriter writer, string list)
    {
        // As in C#: an argument always gets the array, a property only when there was at least one value
        writer.Write(Mode == AssignMode.WriteToVariable
            ? $"let {Variable.Usage} = {list}.ToArray()"
            : $"if {list}.Count > 0 then {_property} <- {list}.ToArray()");
    }

    private void writeRejectionBlock(GeneratedMethod method, ISourceWriter writer, string valueUsage,
        string elementAlias)
    {
        if (!_rejectUnparseableValue)
        {
            return;
        }

        StrictQueryBinding.WriteRejectionBlock(method, writer, Variable.Name, valueUsage, elementAlias);
    }
}