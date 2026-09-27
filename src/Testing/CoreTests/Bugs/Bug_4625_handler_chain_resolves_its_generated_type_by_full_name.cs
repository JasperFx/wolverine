using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Bugs
{
    /// <summary>
    /// GH-4625. In <see cref="TypeLoadMode.Static" />, GH-4151's <c>AssertPreBuiltTypesExist</c> attaches every
    /// handler chain's pre-generated type at startup, and <see cref="HandlerChain" /> found its type with an
    /// <c>Assembly.ExportedTypes</c> walk -- once per chain, over every exported type in the application
    /// assembly. Under Native AOT each walk rebuilds runtime type information from metadata, which made the
    /// walks most of <c>WolverineRuntime.StartAsync</c> for an application with a few dozen handlers.
    ///
    /// <para>The chain now resolves its type by full name first (as HttpChain has since GH-2908) and walks only
    /// if that misses. The walk matched on the simple name alone, so the targeted lookup also stops a same-named
    /// exported type in another namespace from being attached in the generated type's place.</para>
    /// </summary>
    public class Bug_4625_handler_chain_resolves_its_generated_type_by_full_name
    {
        private static readonly string _generatedNamespace = typeof(Bug4625.Generated.Bug4625MessageHandler).Namespace!;

        [Fact]
        public void attaches_the_type_in_the_containing_namespace_not_the_first_simple_name_match()
        {
            // Two exported classes share the generated type's simple name. The walk always takes whichever the
            // runtime enumerates first -- not declaration order, and not something to pin -- so ask for the
            // order and aim at the other one. Only the full-name lookup attaches it.
            var sameName = typeof(Bug4625.Bug4625Message).Assembly.ExportedTypes
                .Where(x => x.Name == nameof(Bug4625.Generated.Bug4625MessageHandler))
                .ToArray();
            sameName.Length.ShouldBe(2);
            var notFirst = sameName[1];

            var chain = buildChain(notFirst.Name);

            attach(chain, notFirst.Namespace!).ShouldBeTrue();

            chain.Handler.ShouldBeOfType(notFirst);
        }

        [Fact]
        public void falls_back_to_the_exported_types_walk_when_the_full_name_misses()
        {
            // A generated type in some other namespace than the one computed for the collection must still
            // attach, as it always has.
            var chain = buildChain(nameof(Bug4625.Generated.Bug4625MessageHandler));

            attach(chain, "Not.The.Generated.Namespace").ShouldBeTrue();

            chain.Handler.ShouldBeAssignableTo<MessageHandler>();
            chain.Handler!.GetType().Name.ShouldBe(nameof(Bug4625.Generated.Bug4625MessageHandler));
        }

        [Fact]
        public void a_type_that_does_not_exist_still_reports_a_miss()
        {
            // AssertPreBuiltTypesExist depends on this to fail the start in Static mode.
            var chain = buildChain("Bug4625HandlerThatWasNeverGenerated");

            attach(chain, _generatedNamespace).ShouldBeFalse();
        }

        private static HandlerChain buildChain(string typeName)
        {
            return new HandlerChain(typeof(Bug4625.Bug4625Message), new HandlerGraph()) { TypeName = typeName };
        }

        private static bool attach(HandlerChain chain, string containingNamespace)
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
                .Build();

            return ((ICodeFile)chain).AttachTypesSynchronously(new GenerationRules(), typeof(Bug4625.Bug4625Message).Assembly,
                host.Services, containingNamespace);
        }
    }
}

namespace CoreTests.Bugs.Bug4625
{
    public record Bug4625Message;
}

// Shares the generated handler's simple name: see the first test above.
namespace CoreTests.Bugs.Bug4625.Elsewhere
{
    public class Bug4625MessageHandler : MessageHandler
    {
        public override Task HandleAsync(MessageContext context, CancellationToken cancellation) => Task.CompletedTask;
    }
}

namespace CoreTests.Bugs.Bug4625.Generated
{
    public class Bug4625MessageHandler : MessageHandler
    {
        public override Task HandleAsync(MessageContext context, CancellationToken cancellation) => Task.CompletedTask;
    }
}
