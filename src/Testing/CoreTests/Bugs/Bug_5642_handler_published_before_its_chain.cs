using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Bugs;

// GH-5642: HandlerChain.AttachTypesSynchronously published the new MessageHandler on chain.Handler *before*
// giving it its chain (Handler.Chain = this). HandlerGraph reads chain.Handler outside its compiling lock, so a
// second message of the same type that asked for its handler in that moment got one whose Chain was null, and
// Executor.Build threw a NullReferenceException reading handler.MessageType. No chain meant no failure policy,
// so the envelope went straight to the dead letter queue with no retry.
//
// The window is a couple of stores wide, so this test widens the odds instead of the window: it attaches the
// generated handler type to a fresh chain over and over, while another thread reads chain.Handler exactly the
// way HandlerGraph's unlocked fast path does and records any handler it sees without a chain.
public class Bug_5642_handler_published_before_its_chain
{
    [Fact]
    public async Task a_handler_is_never_visible_on_its_chain_before_it_has_that_chain()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug5642Handler>();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
            }).StartAsync(TestContext.Current.CancellationToken);

        var graph = host.Services.GetRequiredService<HandlerGraph>();

        // Compile the handler once, the way the first message would, so its generated type exists
        var compiled = (MessageHandler)graph.HandlerFor(typeof(Bug5642Message))!;
        var generatedType = compiled.GetType();
        var typeName = graph.ChainFor(typeof(Bug5642Message))!.TypeName;

        HandlerChain? current = null;
        var done = false;
        var sawChainlessHandler = false;

        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                var chain = Volatile.Read(ref current);
                var handler = chain?.Handler;
                if (handler != null && handler.Chain == null)
                {
                    Volatile.Write(ref sawChainlessHandler, true);
                }
            }
        });
        reader.Start();

        const int attempts = 5_000;
        for (var i = 0; i < attempts && !Volatile.Read(ref sawChainlessHandler); i++)
        {
            var chain = HandlerChain.For<Bug5642Handler>(x => x.Handle(null!), graph);
            chain.TypeName = typeName;
            Volatile.Write(ref current, chain);

            ((ICodeFile)chain).AttachTypesSynchronously(graph.Rules, generatedType.Assembly, host.Services,
                generatedType.Namespace!).ShouldBeTrue();
        }

        Volatile.Write(ref done, true);
        reader.Join();

        sawChainlessHandler.ShouldBeFalse();
    }
}

public record Bug5642Message;

public class Bug5642Handler
{
    public void Handle(Bug5642Message message)
    {
    }
}
