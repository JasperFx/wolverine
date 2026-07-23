using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace MartenTests.AncillaryStores;

/// <summary>
/// GH-3590, ancillary-store variant. Inbox rows for a durable exclusive listener can live in an ancillary store
/// sitting in an entirely different database. The listening node's recovery sweeps those too, and stamps
/// <c>envelope.Store</c> so the follow-on mark-as-handled write lands in the ancillary database rather than
/// silently falling back to the main store — the GH-2318 hazard.
/// </summary>
public class exclusive_listener_recovery_from_ancillary_store : IAsyncLifetime
{
    private IHost _host = null!;
    private string _ancillaryConnectionString = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            _ancillaryConnectionString = await createDatabaseIfNotExistsAsync(conn, "exclusive_ancillary");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();
                opts.Durability.MessageStorageSchemaName = "exclusive_recovery_main";

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "exclusive_recovery_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IExclusiveRecoveryStore>(m =>
                {
                    m.Connection(_ancillaryConnectionString);
                    m.DatabaseSchemaName = "exclusive_recovery_ancillary";
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<RecoveredMessageHandler>();

                opts.ListenToSingleNodeEndpoint(
                    ExclusiveListenerRecoveryCompliance.ExclusiveEndpointName, ListenerScope.Exclusive);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task<string> createDatabaseIfNotExistsAsync(NpgsqlConnection conn, string databaseName)
    {
        if (!await conn.DatabaseExists(databaseName))
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
    }

    [Fact]
    public async Task recovers_dormant_messages_out_of_the_ancillary_store()
    {
        using var tracking = RecoveredMessages.Track();

        var runtime = _host.GetRuntime();
        var destination =
            SingleNodeListenerTransport.ToUri(ExclusiveListenerRecoveryCompliance.ExclusiveEndpointName);

        var ancillary = runtime.Stores.FindAncillaryStore(typeof(IExclusiveRecoveryStore));
        ancillary.ShouldNotBeNull();

        var serializer = runtime.Options.DefaultSerializer;
        var envelopes = Enumerable.Range(0, 5).Select(i =>
        {
            var id = Guid.NewGuid();
            var envelope = new Envelope(new RecoveredMessage(id, i))
            {
                Id = id,
                Destination = destination,
                Status = EnvelopeStatus.Incoming,
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(RecoveredMessage).ToMessageTypeName(),
                SentAt = DateTimeOffset.UtcNow
            };

            envelope.Data = serializer.Write(envelope);

            return envelope;
        }).ToArray();

        await ancillary.Inbox.StoreIncomingAsync(envelopes);

        var expected = envelopes.Select(x => x.Id).ToArray();

        var succeeded = await waitForAsync(() => expected.All(tracking.Contains), 60.Seconds());

        succeeded.ShouldBeTrue(
            $"Expected all {expected.Length} dormant messages in the ancillary store to be recovered by the " +
            $"exclusive listener, but only saw {tracking.Count}");

        // And they were completed against the ancillary store rather than leaking into the main one
        var stillIncoming = await waitForAsync(
            () => ancillary.LoadPageOfGloballyOwnedIncomingAsync(destination, 100).GetAwaiter().GetResult().Count == 0,
            10.Seconds());

        stillIncoming.ShouldBeTrue("The ancillary store's inbox rows should no longer be globally owned");
    }

    private static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100.Milliseconds());
        }

        return condition();
    }
}

public interface IExclusiveRecoveryStore : IDocumentStore;
