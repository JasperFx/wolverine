using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests;

/// <summary>
/// GH-5133. Under MultipleHandlerBehavior.Separated each handler of a message type is its own chain. Every one
/// of them used to claim the same logical deduplication id, so the first claim won and the other handlers
/// dropped the message as a duplicate of work they never did.
/// </summary>
public class deduplication_with_separated_handlers : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dedup_separated");
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ShipmentsModuleHandler))
                    .IncludeType(typeof(InvoicingModuleHandler));
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        ShipmentsModuleHandler.Received.Clear();
        InvoicingModuleHandler.Received.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task each_separated_handler_runs_once_per_logical_id()
    {
        var options = new DeliveryOptions { DeduplicationId = "order-1|placed" };

        await theHost.TrackActivity().PublishMessageAndWaitAsync(new SeparatedDeduplicatedMessage("first"), options);

        // Both handlers ran. Before GH-5133 the first claim of "order-1|placed" won and the other handler
        // discarded the message as a duplicate
        ShipmentsModuleHandler.Received.ShouldBe(["first"]);
        InvoicingModuleHandler.Received.ShouldBe(["first"]);

        // Same logical id again: still deduplicated, for each handler
        await theHost.TrackActivity().PublishMessageAndWaitAsync(new SeparatedDeduplicatedMessage("second"),
            new DeliveryOptions { DeduplicationId = "order-1|placed" });

        ShipmentsModuleHandler.Received.ShouldBe(["first"]);
        InvoicingModuleHandler.Received.ShouldBe(["first"]);
    }
}

public record SeparatedDeduplicatedMessage(string Name);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class ShipmentsModuleHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    public void Handle(SeparatedDeduplicatedMessage message)
    {
        lock (Received) Received.Add(message.Name);
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class InvoicingModuleHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    public void Handle(SeparatedDeduplicatedMessage message)
    {
        lock (Received) Received.Add(message.Name);
    }
}
