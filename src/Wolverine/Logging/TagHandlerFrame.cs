using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Logging;

internal class TagHandlerPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (chain.HandlerCalls().Length == 1)
            {
                chain.Middleware.Add(new TagHandlerFrame(chain));
            }
        }
    }
}

internal class TagHandlerFrame : SyncFrame
{
    private readonly IChain _chain;

    public TagHandlerFrame(IChain chain)
    {
        _chain = chain;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_chain.HandlerCalls().Length == 1)
        {
            var handlerTypeName = _chain.HandlerCalls()[0].HandlerType.FullNameInCode();
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.MessageHandler}\", \"{handlerTypeName}\");");
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{handlerTypeName}\");");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_chain.HandlerCalls().Length == 1)
        {
            // F# has no null-conditional operator, and SetTag returns the Activity (which must be
            // discarded), so guard Activity.Current explicitly and pipe each call to `ignore`.
            var current = $"{typeof(Activity).FSharpName()}.{nameof(Activity.Current)}";
            var handlerTypeName = _chain.HandlerCalls()[0].HandlerType.FullNameInCode();

            writer.Write($"BLOCK:if not (isNull {current}) then");
            writer.Write(
                $"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.MessageHandler}\", \"{handlerTypeName}\") |> ignore");
            writer.Write(
                $"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{handlerTypeName}\") |> ignore");
            writer.FinishBlock();
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}