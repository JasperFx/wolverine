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

        // GH-4426: the NAMES of the handler types codegen is about to emit, plus the message types they
        // dispatch. Both feed the Native AOT rooting companion that HandlerRegistryCodeFile emits — the
        // names because those types do not exist as runtime Types while `codegen write` is running, and
        // the message types because the routers are closed over them reflectively at startup.
        var generatedHandlerTypeNames = new List<string>();
        var chainMessageTypes = new List<Type>();

        // GH-4778. Chain.tryApplyResponseAware closes Applier<T> over each chain's IResponseAware return
        // type at STARTUP, under TypeLoadMode.Static included, so ILC needs those instantiations rooted.
        // Collected with the same ReturnVariablesOfType walk the close itself uses -- a CanBeCastTo test
        // over the handler calls' created variables, no generic close of its own.
        var responseAwareTypes = new List<Type>();

        foreach (var chain in Chains)
        {
            responseAwareTypes.AddRange(chain.ReturnVariablesOfType(typeof(IResponseAware))
                .Select(x => x.VariableType));
            foreach (var handlerChain in chain.ByEndpoint)
            {
                responseAwareTypes.AddRange(handlerChain.ReturnVariablesOfType(typeof(IResponseAware))
                    .Select(x => x.VariableType));
            }

            if (chain.Handlers.Any())
            {
                generatedHandlerTypeNames.Add(chain.TypeName);
                yield return chain;
            }

            foreach (var handlerChain in chain.ByEndpoint)
            {
                generatedHandlerTypeNames.Add(handlerChain.TypeName);
                yield return handlerChain;
            }

            chainMessageTypes.Add(chain.MessageType);

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

        yield return new HandlerRegistryCodeFile(handlerTypes, messageTypes, generatedHandlerTypeNames,
            chainMessageTypes, responseAwareTypes);
    }
}