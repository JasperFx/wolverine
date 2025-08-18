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
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.MessageHandler}\", \"{_chain.HandlerCalls()[0].HandlerType.FullNameInCode()}\");");
        }
        
        Next?.GenerateCode(method, writer);
    }
}