using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace EfCoreTests.Bugs;

public class OverrideStorageOutboxDbContext(DbContextOptions<OverrideStorageOutboxDbContext> options) : DbContext(options);

[Collection("postgresql")]
public class Bug_EfCore_outbox_override_storage : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "DROP SCHEMA IF EXISTS ef_override_main CASCADE; DROP SCHEMA IF EXISTS ef_override_ancillary CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContext<OverrideStorageOutboxDbContext>(
                    x => x.UseNpgsql(_dataSource));

                opts.PersistMessagesWithPostgresql(_dataSource, "ef_override_main");
                opts.PersistMessagesWithPostgresql(_dataSource, "ef_override_ancillary", MessageStoreRole.Ancillary)
                    .Enroll<OverrideStorageOutboxDbContext>();

                opts.UseEntityFrameworkCoreTransactions();
                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _dataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task dbcontext_outbox_should_persist_outgoing_to_overridden_ancillary_store()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var ancillaryStore = runtime.Stores.FindAncillaryStore(typeof(OverrideStorageOutboxDbContext));
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            Status = EnvelopeStatus.Outgoing,
            OwnerId = 5,
            Data = [1, 2, 3],
            MessageType = "ef-core-override-storage",
            ContentType = EnvelopeConstants.JsonContentType,
            Destination = new Uri("rabbitmq://queue/ef-core-override-storage")
        };

        using (var scope = _host.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<OverrideStorageOutboxDbContext>>()
                .ShouldBeOfType<DbContextOutbox<OverrideStorageOutboxDbContext>>();

            outbox.OverrideStorage(ancillaryStore);

            await outbox.Transaction!.PersistOutgoingAsync(envelope);
            await outbox.SaveChangesAndFlushMessagesAsync();
        }

        var mainOutgoing = await runtime.Storage.Admin.AllOutgoingAsync();
        mainOutgoing.ShouldNotContain(x => x.Id == envelope.Id);

        var ancillaryOutgoing = await ancillaryStore.Admin.AllOutgoingAsync();
        ancillaryOutgoing.Single(x => x.Id == envelope.Id).MessageType.ShouldBe("ef-core-override-storage");
    }
}