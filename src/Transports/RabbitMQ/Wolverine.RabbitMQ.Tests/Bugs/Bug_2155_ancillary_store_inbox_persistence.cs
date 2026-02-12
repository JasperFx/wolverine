using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Util;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public interface IAncillaryStore2155 : IDocumentStore;

public record AncillaryMessage2155(Guid Id);

[MartenStore(typeof(IAncillaryStore2155))]
public static class AncillaryMessage2155Handler
{
    [Transactional]
    public static void Handle(AncillaryMessage2155 message, IDocumentSession session)
    {
        // Just touch the session so the transactional middleware commits
        session.Store(new AncillaryDoc2155 { Id = message.Id });
    }
}

public class AncillaryDoc2155
{
    public Guid Id { get; set; }
}

public class Bug_2155_ancillary_store_inbox_persistence : IAsyncLifetime
{
    private IHost _host;
    private string _queueName;

    public async Task InitializeAsync()
    {
        _queueName = RabbitTesting.NextQueueName();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Main Marten store
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2155_main";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "bug2155_main");

                // Ancillary Marten store on same database but different schema
                opts.Services.AddMartenStore<IAncillaryStore2155>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug2155_ancillary";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine(x => x.SchemaName = "bug2155_ancillary");

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<AncillaryMessage2155>().ToRabbitQueue(_queueName).UseDurableOutbox();
                opts.ListenToRabbitQueue(_queueName).UseDurableInbox();

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
    public async Task incoming_envelope_should_be_marked_as_handled_in_main_store()
    {
        var message = new AncillaryMessage2155(Guid.NewGuid());

        await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(message);

        // Give a moment for the async mark-as-handled to complete
        await Task.Delay(500);

        // The main store should have the envelope marked as Handled (not stuck as Incoming)
        var runtime = _host.GetRuntime();
        var incoming = await runtime.Storage.Admin.AllIncomingAsync();
        var stuck = incoming.Where(x =>
            x.MessageType == typeof(AncillaryMessage2155).ToMessageTypeName()
            && x.Status == EnvelopeStatus.Incoming).ToList();

        stuck.ShouldBeEmpty("Incoming envelopes should not be stuck in 'Incoming' status in the main store");
    }
}
