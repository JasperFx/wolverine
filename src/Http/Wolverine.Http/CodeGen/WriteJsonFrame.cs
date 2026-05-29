using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.CodeGen;

public class WriteJsonFrame : AsyncFrame
{
    private readonly Variable _resourceVariable;

    public WriteJsonFrame(Variable resourceVariable)
    {
        _resourceVariable = resourceVariable;
        uses.Add(resourceVariable);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Writing the response body to JSON because this was the first 'return variable' in the method signature");
        writer.Write($"await {nameof(HttpHandler.WriteJsonAsync)}(httpContext, {_resourceVariable.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Writing the response body to JSON because this was the first 'return variable' in the method signature");

        // WriteJsonAsync is an inherited *instance* method on HttpHandler, so it must be qualified with
        // the generated member's `this` self identifier (jasperfx#393).
        var call = $"this.{nameof(HttpHandler.WriteJsonAsync)}(httpContext, {_resourceVariable.Usage})";
        writer.Write(method.AsyncMode == AsyncMode.AsyncTask ? $"do! {call}" : call);

        Next?.GenerateFSharpCode(method, writer);
    }
}