using JasperFx.CodeGeneration;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph
{
    string ICodeFileCollection.ChildNamespace => "WolverineHandlers";

    public GenerationRules Rules { get; internal set; } = null!;

    IReadOnlyList<ICodeFile> ICodeFileCollection.BuildFiles()
    {

        return explodeAllFiles().ToList();
    }

    private IEnumerable<ICodeFile> explodeAllFiles()
    {
        var handlerTypes = new List<Type>();
        var dispatchedMessageTypes = new List<Type>();
        var siblingGeneratedTypeNames = new List<string>();
        var generatedNamespace = ((ICodeFileCollection)this).ToNamespace(Rules);

        foreach (var chain in Chains)
        {
            if (chain.Handlers.Any())
            {
                yield return chain;
                dispatchedMessageTypes.Add(chain.MessageType);
                siblingGeneratedTypeNames.Add($"{generatedNamespace}.{chain.TypeName}");
            }

            foreach (var handlerChain in chain.ByEndpoint)
            {
                yield return handlerChain;
                dispatchedMessageTypes.Add(handlerChain.MessageType);
                siblingGeneratedTypeNames.Add($"{generatedNamespace}.{handlerChain.TypeName}");
            }

            handlerTypes.AddRange(chain.HandlerCalls().Select(x => x.HandlerType));
            foreach (var handlerChain in chain.ByEndpoint)
            {
                handlerTypes.AddRange(handlerChain.HandlerCalls().Select(x => x.HandlerType));
            }
        }

        // Pre-generated handler registry for TypeLoadMode.Static cold-start (Wolverine#1577 Tier 1,
        // GH-2906): capture the discovered handler types AND the conventional message types so startup
        // can skip both assembly scans.
        //
        // The conventional message-type scan is only performed while actually generating code
        // (`codegen write`); BuildFiles is also enumerated during TypeLoadMode.Static *attach*, where a
        // scan here would defeat the purpose. Same WithinCodegenCommand guard as
        // HandlerGraph.shouldConsumeStaticRegistry. (Handler types come from the already-built chains,
        // so they never need a scan.)
        var messageTypes = DynamicCodeBuilder.WithinCodegenCommand
            ? Discovery.DiscoverConventionalMessageTypes()
            : [];

        yield return new HandlerRegistryCodeFile(handlerTypes, messageTypes, dispatchedMessageTypes,
            siblingGeneratedTypeNames);
    }
}