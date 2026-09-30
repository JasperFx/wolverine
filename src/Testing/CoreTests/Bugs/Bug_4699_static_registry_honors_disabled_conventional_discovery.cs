using System.Reflection;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Module2;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// GH-4699. <see cref="TypeLoadMode.Static" /> (and its opt-in twin <c>UseStaticRegistries()</c>) consumed the
/// pre-generated <see cref="HandlerRegistry" /> through <c>HandlerDiscovery.FindCallsFromTypes</c>, which unioned
/// the registry's captured handler types with the explicitly registered ones and filtered NOTHING. The
/// <c>_conventionalDiscoveryDisabled</c> flag that <c>FindCalls</c> honors on the scanning path was simply not
/// consulted, so <c>DisableConventionalDiscovery()</c> did nothing at all once the registry was in play.
///
/// <para>The reported symptom was the loud one -- <see cref="MissingPreBuiltTypesException" /> naming chains the
/// application had excluded. The silent case is the one that matters more: with the registry in sync with the
/// committed chain files, the excluded handlers are registered AND dispatched, and the stale generated body still
/// attaches because a chain keeps its message-type-derived TypeName. Nothing fails; the application just runs
/// handlers its configuration says it does not have.</para>
///
/// <para>This never surfaced in the existing Static + <c>DisableConventionalDiscovery</c> coverage because
/// <c>codegen write</c> runs under <c>WithinCodegenCommand</c>, where <c>shouldConsumeStaticRegistry</c> returns
/// false. The emitting run honors the flag, so the registry it writes is already narrow and self-consistent. The
/// divergence therefore has to be constructed: <see cref="Gh4699StaticRegistry" /> is a committed registry listing
/// BOTH handler types, and the hosts below permit only one of them.</para>
/// </summary>
public class Bug_4699_static_registry_honors_disabled_conventional_discovery
{
    private static readonly Assembly TheAssemblyHoldingTheRegistry = typeof(Gh4699StaticRegistry).Assembly;

    [Fact]
    public async Task registry_handler_types_do_not_survive_disabled_conventional_discovery()
    {
        // The silent case. UseStaticRegistries() rather than TypeLoadMode.Static so that the handler graph can
        // actually be built and inspected -- Static mode's AssertPreBuiltTypesExist would fail the start first,
        // and the registry consumption path is identical either way (shouldConsumeStaticRegistry).
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = TheAssemblyHoldingTheRegistry;
                opts.UseStaticRegistries();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Gh4699IncludedWorker>();
            }).StartAsync(TestContext.Current.CancellationToken);

        var handlers = host.GetRuntime().Handlers;

        // The explicitly included handler is the whole point of DisableConventionalDiscovery + IncludeType, so
        // it has to stay.
        var included = handlers.ChainFor<Gh4699IncludedMessage>();
        included.ShouldNotBeNull();
        included.Handlers.Single().HandlerType.ShouldBe(typeof(Gh4699IncludedWorker));

        // ...and the one the registry smuggled in must be gone. This is the assertion that failed before the fix.
        handlers.ChainFor<Gh4699ExcludedMessage>().ShouldBeNull();
        handlers.CanHandle(typeof(Gh4699ExcludedMessage)).ShouldBeFalse();
    }

    [Fact]
    public async Task the_static_mode_startup_assertion_only_names_permitted_chains()
    {
        // The loud case as reported. Module2 has no pre-generated types at all, so Static mode cannot start
        // either way -- what changes is WHICH chains the failure is about. A chain the configuration excluded
        // has no business being in that list.
        var ex = await Should.ThrowAsync<MissingPreBuiltTypesException>(() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = TheAssemblyHoldingTheRegistry;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Gh4699IncludedWorker>();
            }).StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(Gh4699IncludedMessage));
        ex.Message.ShouldNotContain(nameof(Gh4699ExcludedMessage));
    }
}
