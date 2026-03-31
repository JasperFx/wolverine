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
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace MartenTests.Bugs;

#region Test Infrastructure

public interface IAncillaryStore2382 : IDocumentStore;

// Message handled by ancillary store
public record AncillaryMessage2382(Guid Id);

// Message handled by main store
public record MainStoreMessage2382(Guid Id);

// Handler targeting the ancillary store
[MartenStore(typeof(IAncillaryStore2382))]
public static class AncillaryMessage2382Handler
{
    [Transactional]
    public static void Handle(AncillaryMessage2382 message, IDocumentSession session)
    {
        session.Store(new AncillaryDoc2382 { Id = message.Id });
    }
}

// Handler using the main store
public static class MainStoreMessage2382Handler
{
    [Transactional]
    public static void Handle(MainStoreMessage2382 message, IDocumentSession session)
    {
        session.Store(new MainDoc2382 { Id = message.Id });
    }
}

public class AncillaryDoc2382
{
    public Guid Id { get; set; }
}

public class MainDoc2382
{
    public Guid Id { get; set; }
}

#endregion

/// <summary>
/// Tests that when a handler targets an ancillary store, incoming envelopes
/// are persisted in the ancillary store's inbox (not the main store),
/// ensuring transactional atomicity between the handler's side effects and
/// the envelope status update.
///
/// This matters when the ancillary store targets a different database.
/// </summary>
public class Bug_2382_ancillary_store_inbox : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Main Marten store
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2382_main";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "bug2382_main");

                // Ancillary Marten store — same PostgreSQL server but different schema
                // (simulates a separate database for inbox isolation)
                opts.Services.AddMartenStore<IAncillaryStore2382>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2382_ancillary";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.SchemaName = "bug2382_ancillary");

                // Use durable local queues to exercise the DurableReceiver code path
                opts.LocalQueue("ancillary").UseDurableInbox();
                opts.LocalQueue("main").UseDurableInbox();

                opts.PublishMessage<AncillaryMessage2382>().ToLocalQueue("ancillary");
                opts.PublishMessage<MainStoreMessage2382>().ToLocalQueue("main");

                opts.Policies.AutoApplyTransactions();
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
    public async Task ancillary_store_handler_should_persist_envelope_in_ancillary_inbox()
    {
        var message = new AncillaryMessage2382(Guid.NewGuid());

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(message);

        await Task.Delay(500);

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // The main store should NOT have any lingering incoming envelopes for this message type
        var mainIncoming = await runtime.Storage.Admin.AllIncomingAsync();
        mainIncoming
            .Where(e => e.MessageType == typeof(AncillaryMessage2382).ToMessageTypeName()
                        && e.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty("Ancillary message envelope should not be stuck as Incoming in main store");
    }

    [Fact]
    public async Task main_store_handler_should_persist_envelope_in_main_inbox()
    {
        var message = new MainStoreMessage2382(Guid.NewGuid());

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(message);

        await Task.Delay(500);

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // Main store messages should be handled normally — no lingering Incoming
        var mainIncoming = await runtime.Storage.Admin.AllIncomingAsync();
        mainIncoming
            .Where(e => e.MessageType == typeof(MainStoreMessage2382).ToMessageTypeName()
                        && e.Status == EnvelopeStatus.Incoming)
            .ShouldBeEmpty("Main store message envelope should not be stuck as Incoming");
    }

    [Fact]
    public async Task mixed_messages_both_handlers_succeed()
    {
        // Send both an ancillary and main store message — both should be handled successfully
        var ancillaryMessage = new AncillaryMessage2382(Guid.NewGuid());
        var mainMessage = new MainStoreMessage2382(Guid.NewGuid());

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(ancillaryMessage);

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(mainMessage);

        await Task.Delay(500);

        // Verify ancillary store has the document
        var ancillaryStore = _host.Services.GetRequiredService<IAncillaryStore2382>();
        await using var ancillarySession = ancillaryStore.LightweightSession();
        var ancillaryDoc = await ancillarySession.LoadAsync<AncillaryDoc2382>(ancillaryMessage.Id);
        ancillaryDoc.ShouldNotBeNull();

        // Verify main store has the document
        var mainStore = _host.Services.GetRequiredService<IDocumentStore>();
        await using var mainSession = mainStore.LightweightSession();
        var mainDoc = await mainSession.LoadAsync<MainDoc2382>(mainMessage.Id);
        mainDoc.ShouldNotBeNull();

        // Neither should have lingering incoming envelopes
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var incoming = await runtime.Storage.Admin.AllIncomingAsync();
        incoming.Where(e => e.Status == EnvelopeStatus.Incoming).ShouldBeEmpty();
    }

    [Fact]
    public async Task handler_side_effects_committed_to_ancillary_store()
    {
        var message = new AncillaryMessage2382(Guid.NewGuid());

        await _host
            .TrackActivity()
            .SendMessageAndWaitAsync(message);

        // Verify the document was stored in the ancillary store
        var ancillaryStore = _host.Services.GetRequiredService<IAncillaryStore2382>();
        await using var session = ancillaryStore.LightweightSession();
        var doc = await session.LoadAsync<AncillaryDoc2382>(message.Id);
        doc.ShouldNotBeNull();
    }
}
