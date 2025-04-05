using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.CodeGen;

internal class ParsedArrayQueryStringValue : SyncFrame
{
    public ParsedArrayQueryStringValue(ParameterInfo parameter) : this(parameter.ParameterType, parameter.Name!)
    {
    }

    public ParsedArrayQueryStringValue(PropertyInfo property) : this(property.PropertyType, property.Name!)
    {
    }

    public ParsedArrayQueryStringValue(Type type, string name)
    {
        Variable = new QuerystringVariable(type, name, this);
    }

    public QuerystringVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var elementType = Variable.VariableType.GetElementType();
        if (elementType == typeof(string))
        {
            writer.Write($"var {Variable.Usage} = httpContext.Request.Query[\"{Variable.Usage}\"].ToArray();");
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

            writer.Write($"var {Variable.Usage} = {Variable.Usage}_List.ToArray();");
        }

        Next?.GenerateCode(method, writer);
    }
}