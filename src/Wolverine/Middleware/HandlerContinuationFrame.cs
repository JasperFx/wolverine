using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Middleware;

internal class HandlerContinuationFrame : SyncFrame
{
    private readonly Variable _variable;

    public HandlerContinuationFrame(MethodCall call)
    {
        _variable = call.Creates.FirstOrDefault(x => x.VariableType == typeof(HandlerContinuation)) ??
                    throw new ArgumentOutOfRangeException(nameof(call),"Supplied call does not create a HandlerContinuation");
        _variable.OverrideName(_variable.Usage + ++Count);

        uses.Add(_variable);
    }

    private static int Count { get; set; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Evaluate whether or not the execution should stop based on the HandlerContinuation value");
        if (method.AsyncMode == AsyncMode.AsyncTask)
        {
            writer.Write(
                $"if ({_variable.Usage} == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)}) return;");
        }
        else
        {
            writer.Write(
                $"if ({_variable.Usage} == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)}) return {typeof(Task).FullNameInCode()}.{nameof(Task.CompletedTask)};");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# has no early `return`; render the remainder of the chain inside the `else` branch.
        writer.WriteComment("Evaluate whether or not the execution should stop based on the HandlerContinuation value");
        var condition =
            $"{_variable.Usage} = {typeof(HandlerContinuation).FSharpName()}.{nameof(HandlerContinuation.Stop)}";
        FSharpEmitHelpers.WriteAbortGuard(writer, method, condition, Next);
    }
}