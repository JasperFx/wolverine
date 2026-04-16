using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class TagHttpHandlerPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            chain.Middleware.Insert(0, new TagHttpHandlerFrame(chain));
        }
    }
}

internal class TagHttpHandlerFrame : SyncFrame
{
    private readonly HttpChain _chain;

    public TagHttpHandlerFrame(HttpChain chain)
    {
        _chain = chain;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var handlerTypeName = _chain.Method.HandlerType.FullNameInCode();
        writer.WriteLine(
            $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{handlerTypeName}\");");

        Next?.GenerateCode(method, writer);
    }
}
