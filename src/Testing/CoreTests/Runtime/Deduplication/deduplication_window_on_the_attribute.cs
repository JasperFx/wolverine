using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Deduplication;

/// <summary>
/// <c>[Deduplicated(WindowInSeconds = ...)]</c> gives one handler its own claim lifetime, the way
/// <c>[DeduplicatedWithResponse]</c> already does for HTTP. The behaviour against a real store is pinned in
/// PostgresqlTests, MartenTests, FisherTests and PolecatTests; these cover the attribute and the codegen.
/// </summary>
public class deduplication_window_on_the_attribute
{
    private static DeduplicationRequirement requirementFrom(DeduplicatedAttribute attribute)
    {
        var chain = HandlerChain.For<WindowedDeduplicationHandler>(x => x.Handle(null!), null!);
        attribute.Modify(chain, new GenerationRules(), null!);

        return chain.Deduplication.ShouldNotBeNull();
    }

    private static async Task<IHost> hostFor(Type handlerType)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType))
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static string sourceFor<T>(IHost host)
    {
        var handlers = host.GetRuntime().Handlers;
        handlers.HandlerFor<T>();

        return handlers.ChainFor<T>().ShouldNotBeNull().SourceCode.ShouldNotBeNull();
    }

    [Fact]
    public void zero_leaves_the_window_to_the_durability_settings()
    {
        requirementFrom(new DeduplicatedAttribute()).Window.ShouldBeNull();
    }

    [Fact]
    public void a_positive_window_is_carried_on_the_requirement()
    {
        var requirement = requirementFrom(new DeduplicatedAttribute { WindowInSeconds = 600 });

        requirement.Window.ShouldBe(TimeSpan.FromMinutes(10));
        requirement.ToString().ShouldContain("Window = 00:10:00");
    }

    [Fact]
    public async Task a_negative_window_is_refused_when_the_chain_is_built()
    {
        using var host = await hostFor(typeof(NegativeWindowDeduplicationHandler));

        var exception = Should.Throw<Exception>(() =>
            host.GetRuntime().Handlers.HandlerFor<NegativeWindowDeduplicationMessage>());

        var refusal = exception as InvalidOperationException
                      ?? exception.InnerException.ShouldBeOfType<InvalidOperationException>();

        refusal.Message.ShouldContain("WindowInSeconds = -1");
    }

    [Fact]
    public async Task the_generated_claim_passes_the_window()
    {
        using var host = await hostFor(typeof(WindowedDeduplicationHandler));

        var source = sourceFor<WindowedDeduplicationMessage>(host);

        // The window sits between the id and the (absent) ancillary store marker
        source.ShouldContain($", System.TimeSpan.FromTicks({TimeSpan.FromMinutes(10).Ticks}), null, ");
        source.ShouldContain(nameof(MessageDeduplicatorExtensions.TryClaimForEnvelopeAsync));
    }

    [Fact]
    public async Task a_chain_without_a_window_claims_for_the_host_wide_window()
    {
        // Pre-generated handler code from before WindowInSeconds existed calls the overload without a window, and
        // still compiles against it. A chain generated now passes a null window, which is the host-wide one.
        using var host = await hostFor(typeof(DefaultWindowDeduplicationHandler));

        var source = sourceFor<DefaultWindowDeduplicationMessage>(host);

        source.ShouldContain(nameof(MessageDeduplicatorExtensions.TryClaimForEnvelopeAsync));
        source.ShouldNotContain("System.TimeSpan.FromTicks(");
    }

    /// <summary>
    /// D1 (durable compliance findings, 2026-10-22). A message handler claims on behalf of the envelope it is
    /// handling, so the copy inbox recovery brings back after a node died mid-handler can take over the claim that
    /// node stranded, instead of being refused as a duplicate of its own earlier attempt.
    /// </summary>
    [Fact]
    public async Task a_message_handler_claims_on_behalf_of_its_envelope()
    {
        using var host = await hostFor(typeof(DefaultWindowDeduplicationHandler));

        var source = sourceFor<DefaultWindowDeduplicationMessage>(host);

        source.ShouldContain(
            $"{typeof(MessageDeduplicatorExtensions).FullName}.{nameof(MessageDeduplicatorExtensions.TryClaimForEnvelopeAsync)}(");
        source.ShouldContain(", context.Envelope, null, null, ");
    }
}

public record WindowedDeduplicationMessage;

public record DefaultWindowDeduplicationMessage;

public record NegativeWindowDeduplicationMessage;

[WolverineIgnore]
public class WindowedDeduplicationHandler
{
    [Deduplicated(WindowInSeconds = 600)]
    public void Handle(WindowedDeduplicationMessage message)
    {
    }
}

[WolverineIgnore]
public class DefaultWindowDeduplicationHandler
{
    [Deduplicated]
    public void Handle(DefaultWindowDeduplicationMessage message)
    {
    }
}

[WolverineIgnore]
public class NegativeWindowDeduplicationHandler
{
    [Deduplicated(WindowInSeconds = -1)]
    public void Handle(NegativeWindowDeduplicationMessage message)
    {
    }
}
