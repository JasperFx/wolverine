using JasperFx.CodeGeneration;
using Wolverine.Configuration;

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

        // GH-4765. The roots no package could contribute for itself. A frame built by closing an open
        // generic over the user's saga, aggregate or DbContext type is created while the chain MODEL is
        // built -- which still happens at startup under TypeLoadMode.Static, generated code or not -- so
        // ILC has to keep that instantiation even though nothing statically references it. The frames
        // already exist by the time this runs, so each one can simply name its own closed type.
        var aotRootTypes = new List<Type>();

        foreach (var chain in Chains)
        {
            responseAwareTypes.AddRange(chain.ReturnVariablesOfType(typeof(IResponseAware))
                .Select(x => x.VariableType));
            aotRootTypes.AddRange(aotRootsOf(chain));
            foreach (var handlerChain in chain.ByEndpoint)
            {
                responseAwareTypes.AddRange(handlerChain.ReturnVariablesOfType(typeof(IResponseAware))
                    .Select(x => x.VariableType));
                aotRootTypes.AddRange(aotRootsOf(handlerChain));
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
            chainMessageTypes, responseAwareTypes, aotRootTypes);
    }

    /// <summary>
    ///     GH-4765: the closed types a chain's own frames say they need rooted.
    /// </summary>
    /// <remarks>
    ///     All three frame lists, because a reflectively-closed frame can be contributed to any of them —
    ///     the saga enrollment frame lands in <see cref="IChain.Middleware" /> while the Marten compiled-query
    ///     frames land in <see cref="IChain.Postprocessors" />.
    ///
    ///     <para>Known gap: a frame that an <c>IVariableSource</c> builds while the method body is being
    ///     generated is in none of these lists yet when this runs, so it cannot be collected here.
    ///     <c>SagaStorageVariableSource</c> is the one such case, and it closes the same
    ///     <c>EnrollAndFetchSagaStorageFrame&lt;,&gt;</c> that the middleware path already roots for any saga
    ///     reaching it through <c>LightweightSagaPersistenceFrameProvider</c>.</para>
    /// </remarks>
    private static IEnumerable<Type> aotRootsOf(IChain chain)
    {
        return chain.Middleware
            .Concat(chain.Postprocessors)
            .Concat(chain.PostCommitPostprocessors)
            .OfType<IAotRootSource>()
            .SelectMany(x => x.AotRoots());
    }
}