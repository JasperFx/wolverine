using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Middleware;

internal class HandlerContinuationFrame : SyncFrame
{
    private readonly Variable _variable;

    /// <summary>
    /// GH-4877. <paramref name="index"/> is the frame's position among the continuation-carrying frames of
    /// ITS OWN chain (<see cref="IChain.NextContinuationVariableIndex"/>), the convention GH-4714 set for
    /// the validation and requirement frames: index 0 keeps the bare name, later ones are suffixed so two
    /// in one generated method cannot collide. This frame was the last one still numbering from a
    /// process-wide static, so a chain's generated source depended on how many continuation frames the
    /// process had built before it -- which, under embedded static codegen inside a booted host, varied
    /// run to run and failed a drift gate on suffix-only diffs.
    /// </summary>
    public HandlerContinuationFrame(MethodCall call, int index)
    {
        _variable = call.Creates.FirstOrDefault(x => x.VariableType == typeof(HandlerContinuation)) ??
                    throw new ArgumentOutOfRangeException(nameof(call),"Supplied call does not create a HandlerContinuation");

        if (index > 0)
        {
            _variable.OverrideName(_variable.Usage + index);
        }

        uses.Add(_variable);
    }

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