using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
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
            // GH-5133. Keyed by handler TYPE rather than by handler call: a saga's Start/Handle/NotFound
            // methods are several calls on one type and used to get no tag at all. A chain that combines
            // several handler types (the obsolete ClassicCombineIntoOneLogicalHandler mode) is tagged with
            // all of them. Captured here, at policy time, because SagaChain clears its handler calls while
            // it generates code.
            //
            // GH-5224. The name is resolved (and cached) through HandlerChain.TelemetryHandlerTypeName() so
            // that RecordMessageCausationFrame reports exactly the same string to
            // IWolverineObserver.MessageCausedBy as this frame stamps on the message.handler tag.
            var handlerTypeName = chain.TelemetryHandlerTypeName();

            if (handlerTypeName.IsEmpty()) continue;

            chain.Middleware.Add(new TagHandlerFrame(handlerTypeName));
        }
    }

    /// <summary>
    /// The one place the handler type name used for telemetry is built: the distinct handler types of
    /// the supplied handler calls, by full name in code, joined with ", ". Shared by the
    /// <c>message.handler</c> span tag (<see cref="TagHandlerFrame"/>) and the <c>handlerType</c>
    /// reported to <see cref="Wolverine.Runtime.Agents.IWolverineObserver.MessageCausedBy"/> (RecordMessageCausationFrame) so
    /// the two cannot drift. Returns an empty string when there are no handler calls.
    /// </summary>
    internal static string HandlerTypeNameFor(IEnumerable<MethodCall> handlerCalls)
    {
        return handlerCalls
            .Select(x => x.HandlerType)
            .Distinct()
            .Select(x => x.FullNameInCode())
            .Join(", ");
    }
}

internal class TagHandlerFrame : SyncFrame
{
    private readonly string _handlerTypeName;

    public TagHandlerFrame(string handlerTypeName)
    {
        _handlerTypeName = handlerTypeName;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_handlerTypeName.IsNotEmpty())
        {
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.MessageHandler}\", \"{_handlerTypeName}\");");
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{_handlerTypeName}\");");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_handlerTypeName.IsNotEmpty())
        {
            // F# has no null-conditional operator, and SetTag returns the Activity (which must be
            // discarded), so guard Activity.Current explicitly and pipe each call to `ignore`.
            var current = $"{typeof(Activity).FSharpName()}.{nameof(Activity.Current)}";

            writer.Write($"BLOCK:if not (isNull {current}) then");
            writer.Write(
                $"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.MessageHandler}\", \"{_handlerTypeName}\") |> ignore");
            writer.Write(
                $"{current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{_handlerTypeName}\") |> ignore");
            writer.FinishBlock();
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
