using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MartenTests.Bugs;

/// <summary>
/// Marker interface for the ancillary Marten store used in Bug 2318 tests.
/// </summary>
public interface IAncillaryStore2318 : IDocumentStore;

public record AncillaryCommand2318(Guid Id);
public record SomeMessage2318(Guid Id);
public record AncillaryEvent2318(Guid Id);

public static class Switch2318
{
    public static bool ShouldThrow { get; set; } = true;
}

[MartenStore(typeof(IAncillaryStore2318))]
public static class AncillaryCommand2318Handler
{
    [Transactional]
    public static void Handle(AncillaryCommand2318 message, IDocumentSession session)
    {
        session.Events.Append(message.Id, new AncillaryEvent2318(message.Id));
    }
}

[MartenStore(typeof(IAncillaryStore2318))]
public static class AncillaryEvent2318Handler
{
    public static SomeMessage2318 Handle(AncillaryEvent2318 message, IDocumentSession session)
    {
        return new SomeMessage2318(message.Id);
    }
}

[MartenStore(typeof(IAncillaryStore2318))]
public static class SomeMessage2318Handler
{
    public static void Handle(SomeMessage2318 message, IDocumentSession session)
    {
        if (Switch2318.ShouldThrow)
        {
            throw new Exception("Simulating a failure for Bug 2318");
        }
    }
}

/// <summary>
/// Reproduces https://github.com/JasperFx/wolverine/issues/2318
///
/// When a message routed to an ancillary store fails and goes to DLQ,
/// replaying it from the DLQ should mark it as handled in the ancillary
/// store, not the main store.
/// </summary>
public class Bug_2318_ancillary_dlq_replay : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        Switch2318.ShouldThrow = true;

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Main Marten store
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.AllLocalQueues(x => x.UseDurableInbox());
                opts.Durability.Mode = DurabilityMode.Solo;

                // Ancillary Marten store on same database but different schema
                opts.Services.AddMartenStore<IAncillaryStore2318>(sp =>
                    {
                        var storeOptions = new StoreOptions
                        {
                            Events =
                            {
                                DatabaseSchemaName = "bug2318_ancillary",
                            },
                            DatabaseSchemaName = "bug2318_ancillary"
                        };

                        storeOptions.Connection(Servers.PostgresConnectionString);
                        return storeOptions;
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo)
                    .ProcessEventsWithWolverineHandlersInStrictOrder("bug2318_sub",
                        o => o.IncludeType<AncillaryEvent2318>());

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task replayed_dlq_message_should_not_be_stuck_in_incoming()
    {
        // Step 1: Send a message that will eventually fail in the ancillary store handler
        var message = new AncillaryCommand2318(Guid.NewGuid());

        await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(message);

        // Give time for the message to be dead-lettered
        await Task.Delay(5.Seconds());

        var runtime = _host.GetRuntime();
        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(IAncillaryStore2318));
        ancillaryStore.ShouldNotBeNull("Ancillary store should exist");

        // Verify the dead letter exists in the ancillary store
        var deadLetters = await ancillaryStore.DeadLetters
            .QueryAsync(new DeadLetterEnvelopeQuery { Range = TimeRange.AllTime() }, CancellationToken.None);

        var dlqEntries = deadLetters.Envelopes
            .Where(x => x.Envelope.MessageType == typeof(SomeMessage2318).ToMessageTypeName())
            .ToList();

        dlqEntries.ShouldNotBeEmpty(
            "SomeMessage2318 should be in the ancillary store's DLQ");

        // Step 2: Toggle the switch so the handler succeeds
        Switch2318.ShouldThrow = false;

        // Step 3: Replay the DLQ from the ancillary store
        await ancillaryStore.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery(TimeRange.AllTime()), CancellationToken.None);

        // Trigger the durability agent to recover the replayed messages
        ancillaryStore.StartScheduledJobs(runtime);

        // Wait for the replayed message to be processed
        await Task.Delay(10.Seconds());

        // Step 4: Verify the envelope is NOT stuck as Incoming in the ancillary store
        var incoming = await ancillaryStore.Admin.AllIncomingAsync();

        var stuck = incoming
            .Where(x => x.MessageType == typeof(SomeMessage2318).ToMessageTypeName()
                        && x.Status == EnvelopeStatus.Incoming)
            .ToList();

        stuck.ShouldBeEmpty(
            "Incoming envelopes should not be stuck in 'Incoming' status in the ancillary store. " +
            "The replayed DLQ message should have been marked as handled in the ancillary store, " +
            "not the main store. See https://github.com/JasperFx/wolverine/issues/2318");
    }
}
