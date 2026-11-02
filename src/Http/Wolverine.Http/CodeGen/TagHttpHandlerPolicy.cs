using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Logging;
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
    private MessageActivityVariable? _activity;

    public TagHttpHandlerFrame(HttpChain chain)
    {
        _chain = chain;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // GH-5310: ASP.NET Core's span for the request rather than whatever Activity.Current has become
        _activity = MessageActivityVariable.TryFind(chain);
        foreach (var variable in MessageActivityVariable.Uses(_activity)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var handlerTypeName = _chain.Method.HandlerType.FullNameInCode();
        writer.WriteLine(
            $"{MessageActivityVariable.UsageFor(_activity)}?.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{handlerTypeName}\");");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var handlerTypeName = _chain.Method.HandlerType.FSharpName();
        var current = MessageActivityVariable.FSharpUsageFor(_activity);
        writer.Write(
            $"if not (isNull {current}) then {current}.{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", \"{handlerTypeName}\") |> ignore");

        Next?.GenerateFSharpCode(method, writer);
    }
}
