using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.CodeGen;

public class WriteJsonFrame : AsyncFrame
{
    private readonly Variable _resourceVariable;
    private Variable? _options;

    public WriteJsonFrame(Variable resourceVariable)
    {
        _resourceVariable = resourceVariable;
        uses.Add(resourceVariable);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _options = chain.FindVariable(typeof(JsonSerializerOptions));
        yield return _options;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"await {nameof(EndpointHandler.WriteJsonAsync)}(httpContext, {_resourceVariable.Usage}, {_options!.Usage});");
        Next?.GenerateCode(method, writer);
    }
}