using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.CodeGen;

internal class ParsedArrayQueryStringValue : SyncFrame, IReadQueryStringFrame
{
    private string _property;

    public ParsedArrayQueryStringValue(Type parameterType, string parameterName) 
    {
        Variable = new QuerystringVariable(parameterType, parameterName!, this);
    }

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = QueryStringAssignMode.WriteToProperty;
    }

    public QueryStringAssignMode Mode { get; private set; } = QueryStringAssignMode.WriteToVariable;

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var elementType = Variable.VariableType.GetElementType();
        if (elementType == typeof(string))
        {
            if (Mode == QueryStringAssignMode.WriteToVariable)
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
            var collectionAlias = typeof(List<>).MakeGenericType(elementType).FullNameInCode();
            var elementAlias = elementType.FullNameInCode();

            writer.Write($"var {Variable.Usage}_List = new {collectionAlias}();");
            
            writer.Write($"BLOCK:foreach (var {Variable.Usage}Value in httpContext.Request.Query[\"{Variable.Usage}\"])");

            if (elementType.IsEnum)
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

            writer.FinishBlock(); // foreach blobck
            
            if (Mode == QueryStringAssignMode.WriteToVariable)
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