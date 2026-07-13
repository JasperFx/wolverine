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
        Variable = new HttpElementVariable(parameterType, parameterName!, this);

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
                writer.Write($"var {Variable.Usage} = httpContext.Request.Query[\"{Variable.Usage}\"].ToArray();");
            }
            else
            {
                writer.Write($"{_property} = httpContext.Request.Query[\"{Variable.Usage}\"].ToArray();");
            }
        }
        else
        {
            var collectionAlias = typeof(List<>).MakeGenericType(elementType!).FullNameInCode();
            var elementAlias = elementType!.FullNameInCode();

            writer.Write($"var {Variable.Usage}_List = new {collectionAlias}();");
            
            writer.Write($"BLOCK:foreach (var {Variable.Usage}Value in httpContext.Request.Query[\"{Variable.Usage}\"])");

            if (elementType!.IsEnum)
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse<{elementAlias}>({Variable.Usage}Value, out var {Variable.Usage}ValueParsed))");
            }
            else if (elementType.IsBoolean())
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Usage}Value, out var {Variable.Usage}ValueParsed))");
            }
            else
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Usage}Value, System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}ValueParsed))");
            }

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