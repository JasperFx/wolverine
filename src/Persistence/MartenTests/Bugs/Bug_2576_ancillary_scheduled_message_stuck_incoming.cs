using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace MartenTests.Bugs;

#region Test Infrastructure

public interface IAncillaryStore2576 : IDocumentStore;

public record AncillaryCommand2576(Guid Id);

public record AncillaryEvent2576(Guid Id);

public record SomeMessage2576(Guid Id);

[MartenStore(typeof(IAncillaryStore2576))]
public static class AncillaryCommand2576Handler
{
    [Transactional]
    public static AncillaryEvent2576 Handle(AncillaryCommand2576 message, IDocumentSession session)
    {
        var @event = new AncillaryEvent2576(message.Id);
        session.Events.Append(message.Id, @event);
        return @event;
    }
}

[MartenStore(typeof(IAncillaryStore2576))]
public static class AncillaryEvent2576Handler
{
    public static ScheduledMessage<SomeMessage2576> Handle(AncillaryEvent2576 message, IDocumentSession session)
    {
        session.Events.Append(message.Id, message);
        // Schedule in the past so the scheduled-jobs processor picks it up immediately.
        return new SomeMessage2576(message.Id).ScheduledAt(DateTime.UtcNow.AddDays(-1));
    }
}

[MartenStore(typeof(IAncillaryStore2576))]
public static class SomeMessage2576Handler
{
    [Transactional]
    public static void Handle(SomeMessage2576 message, IDocumentSession session)
    {
        session.Events.Append(message.Id, message);
    }
}

#endregion

/// <summary>
/// FAILING reproducer for https://github.com/JasperFx/wolverine/issues/2576.
///
/// When a handler chain is fully owned by an ancillary Marten store
/// (every handler tagged with [MartenStore(typeof(IAncillaryStore))]),
/// and a handler returns a <see cref="ScheduledMessage{T}"/>, the resulting
/// envelope ends up persisted in the ancillary store's outbox/inbox tables
/// — but when its handler later succeeds, Wolverine writes the
/// "mark as Handled" SQL to the MAIN store instead, leaving the row in
/// the ancillary store stuck in <c>Incoming</c> status forever.
///
/// The expected behavior is that the envelope's mark-as-handled SQL runs
/// against the same store that owns the handler.
/// </summary>
public class Bug_2576_ancillary_scheduled_message_stuck_incoming : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
                opts.Durability.KeepAfterMessageHandling = 1.Hours();

                // Main Marten store
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2576_main";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "bug2576_main");

                // Ancillary Marten store on a separate schema (mirrors the
                // reporter's separate-database setup; schema isolation is
                // sufficient to exercise the routing decision).
                opts.Services.AddMartenStore<IAncillaryStore2576>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2576_ancillary";
                    m.Events.DatabaseSchemaName = "bug2576_ancillary";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.SchemaName = "bug2576_ancillary");

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.AutoApplyTransactions();
                opts.Policies.AllLocalQueues(x => x.UseDurableInbox());
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task scheduled_message_in_ancillary_chain_should_not_be_stuck_incoming()
    {
        var message = new AncillaryCommand2576(Guid.NewGuid());

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(message);

        // The scheduled message has to wake up out of the scheduled-jobs poller
        // and run through the handler. Give it a moment.
        await Task.Delay(5.Seconds());

        var runtime = _host.GetRuntime();
        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(IAncillaryStore2576));

        var ancillaryIncoming = await ancillaryStore.Admin.AllIncomingAsync();

        var someMessageTypeName = typeof(SomeMessage2576).ToMessageTypeName();

        ancillaryIncoming
            .Count(x => x.MessageType == someMessageTypeName && x.Status == EnvelopeStatus.Incoming)
            .ShouldBe(0,
                "The scheduled SomeMessage2576 should not be left in Incoming status in the ancillary store. " +
                "If this is non-zero, Wolverine wrote the 'mark as handled' SQL to the wrong store.");

        ancillaryIncoming
            .Count(x => x.MessageType == someMessageTypeName && x.Status == EnvelopeStatus.Handled)
            .ShouldBe(1,
                "The scheduled SomeMessage2576 should be marked as Handled in the ancillary store " +
                "(the same store that scheduled and processed it).");

        // Cross-check: the main store should NOT have a row for this message
        // type at all — the entire chain belongs to the ancillary store.
        var mainIncoming = await runtime.Storage.Admin.AllIncomingAsync();
        mainIncoming
            .Count(x => x.MessageType == someMessageTypeName)
            .ShouldBe(0,
                "The main store should have no envelope rows for the ancillary-owned SomeMessage2576.");
    }
}
