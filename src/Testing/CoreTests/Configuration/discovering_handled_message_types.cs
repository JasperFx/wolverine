using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Batching;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
///     GH-3974. Two related gaps, both of which forced consumers to re-derive something Wolverine already
///     knows: which message types will be handled, and how a batched element type reaches its handler.
/// </summary>
public class discovering_handled_message_types
{
    [Fact]
    public async Task the_callback_reports_the_resolved_handled_message_types()
    {
        DiscoveredHandlers? discovered = null;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DiscoveryProbeHandler));
                opts.OnHandlersDiscovered(x => discovered = x);
            }).StartAsync(TestContext.Current.CancellationToken);

        discovered.ShouldNotBeNull("the callback never fired");

        discovered.Handles<DiscoveryProbeMessage>().ShouldBeTrue();
        discovered.Handles(typeof(DiscoveryProbeMessage)).ShouldBeTrue();

        // The whole point: a definitive NO for a type nothing handles, so a fallback can be installed
        // without clobbering a real handler
        discovered.Handles<UnhandledProbeMessage>().ShouldBeFalse();

        discovered.MessageTypes.ShouldContain(typeof(DiscoveryProbeMessage));
    }

    [Fact]
    public async Task the_default_batcher_maps_the_element_type_to_an_array()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(BatchedProbeHandler));
                opts.BatchMessagesOf<BatchedProbe>();
            }).StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.TryFindBatchMessageType(typeof(BatchedProbe), out var batchMessageType).ShouldBeTrue();
        batchMessageType.ShouldBe(typeof(BatchedProbe[]));

        options.BatchMappings.ShouldContain(x =>
            x.ElementType == typeof(BatchedProbe) && x.BatchMessageType == typeof(BatchedProbe[]));
    }

    /// <summary>
    ///     The case that motivated the issue. <c>IMessageBatcher.BatchMessageType</c> is a free-form
    ///     <see cref="Type" /> — nothing requires <c>T[]</c> — so inferring the handled type from array-ness
    ///     (<c>parameters[0].ParameterType.GetElementType()</c>) is silently wrong for a custom batcher.
    /// </summary>
    [Fact]
    public async Task a_custom_batcher_reports_its_own_batch_message_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CustomBatchProbeHandler));
                opts.BatchMessagesOf<CustomBatchedProbe>(x => x.Batcher = new CustomProbeBatcher());
            }).StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.TryFindBatchMessageType(typeof(CustomBatchedProbe), out var batchMessageType).ShouldBeTrue();

        // NOT CustomBatchedProbe[] -- which is exactly what an array-ness inference would have guessed
        batchMessageType.ShouldBe(typeof(CustomProbeBatch));
        batchMessageType.IsArray.ShouldBeFalse();
    }

    [Fact]
    public async Task reports_false_for_an_element_type_that_is_not_batched()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DiscoveryProbeHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.TryFindBatchMessageType(typeof(BatchedProbe), out _).ShouldBeFalse();
        options.BatchMappings.ShouldBeEmpty();
    }
}

public record DiscoveryProbeMessage;

public record UnhandledProbeMessage;

[WolverineIgnore]
public static class DiscoveryProbeHandler
{
    public static void Handle(DiscoveryProbeMessage message)
    {
    }
}

public record BatchedProbe(string Name);

[WolverineIgnore]
public static class BatchedProbeHandler
{
    public static void Handle(BatchedProbe[] batch)
    {
    }
}

public record CustomBatchedProbe(string Name);

/// <summary>
///     A batch message that is deliberately NOT an array of the element type.
/// </summary>
public record CustomProbeBatch(CustomBatchedProbe[] Items);

[WolverineIgnore]
public static class CustomBatchProbeHandler
{
    public static void Handle(CustomProbeBatch batch)
    {
    }
}

public class CustomProbeBatcher : IMessageBatcher
{
    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        var items = envelopes.Select(x => x.Message).OfType<CustomBatchedProbe>().ToArray();
        yield return new Envelope(new CustomProbeBatch(items), envelopes);
    }

    public Type BatchMessageType => typeof(CustomProbeBatch);
}
