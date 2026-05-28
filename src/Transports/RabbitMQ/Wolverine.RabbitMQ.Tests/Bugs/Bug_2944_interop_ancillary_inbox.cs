using System.Text.Json;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

// Regression test for GH-2944. Reported by @fadrian23.
//
// A message arrives from an external system in INTEROP mode (i.e. published as raw JSON, with no
// Wolverine envelope headers). Its handler targets an ancillary Marten store via [MartenStore].
// Before the fix the durable inbox envelope ended up in the MAIN store's inbox instead of the
// ancillary store's inbox.
//
// Root cause: [MartenStore] is a ModifyChainAttribute, so MartenStoreAttribute.Modify() runs in
// Phase B (lazy, inside HandlerChain.applyCustomizations at first-codegen time). The
// message-type-to-ancillary-store map that WolverineRuntime.HostService builds during
// startMessagingTransportsAsync runs in Phase A (eager, at handler-graph compile). At that point
// chain.AncillaryStoreType is still null on every chain, so the map is empty. When the interop
// envelope arrives, DurableLocalQueue / DurableReceiver looks up the ancillary store by message
// type and finds nothing, so it falls back to the main store.
//
// Fix: MartenStoreEagerPolicy (registered by MartenIntegration alongside the AggregateHandler
// strategy) pre-populates chain.AncillaryStoreType in Phase A by walking the handler-type and
// handler-method for [MartenStore] - matching the discovery rules in
// Chain.applyAttributesAndConfigureMethods. The Phase B Modify() still runs later and is
// idempotent for the AncillaryStoreType assignment plus inserts AncillaryOutboxFactoryFrame.

public interface IAncillaryStore2944 : IDocumentStore;

public record InteropMessage2944(Guid Id);

public class InteropDoc2944
{
    public Guid Id { get; set; }
}

[MartenStore(typeof(IAncillaryStore2944))]
public static class InteropMessage2944Handler
{
    [Transactional]
    public static void Handle(InteropMessage2944 message, IDocumentSession session)
    {
        session.Store(new InteropDoc2944 { Id = message.Id });
    }
}

public class Bug_2944_interop_ancillary_inbox : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;

    public async Task InitializeAsync()
    {
        _queueName = RabbitTesting.NextQueueName();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2944_main";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "bug2944_main");

                opts.Services.AddMartenStore<IAncillaryStore2944>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2944_ancillary";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.SchemaName = "bug2944_ancillary");

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.ListenToRabbitQueue(_queueName)
                    .DefaultIncomingMessage<InteropMessage2944>()
                    .UseDurableInbox();

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
    public async Task interop_message_should_be_persisted_in_ancillary_store_inbox()
    {
        var runtime = _host.GetRuntime();

        var messageId = Guid.NewGuid();
        var message = new InteropMessage2944(messageId);
        var data = JsonSerializer.SerializeToUtf8Bytes(message);

        var transport = runtime.Options.RabbitMqTransport();

        // Publish raw JSON bytes — no Wolverine envelope headers, simulating an external producer.
        var session = await _host.TrackActivity()
            .Timeout(30.Seconds())
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<InteropMessage2944>(_host)
            .ExecuteAndWaitAsync(async Task (_) =>
            {
                await transport.WithAdminChannelAsync(async channel =>
                {
                    var props = new BasicProperties();
                    await channel.BasicPublishAsync(string.Empty, _queueName, true, props, data);
                });
            });

        session.Received.SingleEnvelope<InteropMessage2944>().ShouldNotBeNull();

        // Brief settle so the inbox row commits before we query - the tracker fires on Received,
        // not on inbox commit.
        await Task.Delay(1000);

        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(IAncillaryStore2944));

        var ancillaryIncoming = await ancillaryStore.Admin.AllIncomingAsync();
        var messageTypeName = typeof(InteropMessage2944).ToMessageTypeName();

        ancillaryIncoming.ShouldContain(
            x => x.MessageType == messageTypeName,
            "The interop message should have been persisted in the ancillary store's inbox");

        var mainIncoming = await runtime.Storage.Admin.AllIncomingAsync();
        mainIncoming.ShouldNotContain(
            x => x.MessageType == messageTypeName,
            "The interop message should NOT be persisted in the main store's inbox");
    }
}
