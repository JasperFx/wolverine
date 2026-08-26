using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

// GH-4151: TypeLoadMode.Static loads pre-built handler types out of WolverineOptions.ApplicationAssembly,
// but `codegen write` emits its source into the *entry* project. When handlers live in a class library and
// the app points ApplicationAssembly at that library, the two disagree and nothing detected it -- not codegen
// write, not the build, not host start. The host booted healthy, and then StaticTypeLoader threw
// ExpectedTypeMissingException on the first dispatched message, from inside HandlerGraph.HandlerFor while the
// *executor* was being built. No HandlerChain instance exists at that point, so no failure policy could
// apply, and the pipeline's last-resort recovery simply acked the envelope away: on a durable transport the
// row was marked Handled with attempts=0 and then swept by ordinary inbox cleanup. A durable message was
// silently discarded on a configuration error, with no DLQ row and a host that stayed healthy.
//
// Two independent fixes, one per half:
//   * the assembly mismatch now fails the deploy at startup instead of the first message, and
//   * an envelope whose executor cannot be built is dead-lettered rather than completed -- whatever the
//     reason, since every cause takes the same path.
public class Bug_4151_executor_build_failure_loses_message
{
    [Fact]
    public async Task static_mode_without_pre_built_types_fails_the_host_start()
    {
        // No pre-built types were ever generated into this assembly, which is exactly the state the
        // entry-project-vs-library split leaves a Static mode app in.
        var ex = await Should.ThrowAsync<MissingPreBuiltTypesException>(() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(Bug_4151_executor_build_failure_loses_message).Assembly;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

                opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug4151PingHandler>();
            }).StartAsync(TestContext.Current.CancellationToken));

        // The message has to name the chain that would have failed and the assembly that was searched,
        // because "it threw on the first Ping" was the whole problem.
        ex.Message.ShouldContain(nameof(Bug4151Ping));
        ex.Message.ShouldContain("CoreTests");
    }

    [Fact]
    public async Task an_envelope_whose_executor_cannot_be_built_is_dead_lettered_not_completed()
    {
        // A sticky-handler misconfiguration is the same failure at a different origin: two sticky handlers
        // and no unsticky one, so HandlerGraph.HandlerFor(type, endpoint) has nothing to hand back for any
        // other endpoint and throws while the executor is being built.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug4151GreenHandler))
                    .IncludeType(typeof(Bug4151BlueHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
                c.EndpointFor(new Uri("local://maroon")).SendAsync(new Bug4151Ping()).AsTask());

        // The envelope used to be recorded as MessageFailed only: acked away and gone, with nothing in the
        // message store to distinguish it from a message that was handled successfully.
        session.MovedToErrorQueue.SingleEnvelope<Bug4151Ping>().ShouldNotBeNull();
    }
}

public record Bug4151Ping;

public class Bug4151PingHandler
{
    public static void Handle(Bug4151Ping ping)
    {
    }
}

[StickyHandler("bug4151-green")]
public static class Bug4151GreenHandler
{
    public static void Handle(Bug4151Ping ping)
    {
    }
}

[StickyHandler("bug4151-blue")]
public static class Bug4151BlueHandler
{
    public static void Handle(Bug4151Ping ping)
    {
    }
}
