using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

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
}