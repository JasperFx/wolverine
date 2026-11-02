using IntegrationTests;
using JasperFx;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport.NServiceBus;

namespace PostgresqlTests.Transport.NServiceBus;

/// <summary>
/// GH-5342. An NServiceBus queue table belongs to NServiceBus. With AutoProvision on for the interop transport,
/// an externally owned queue is still never created or dropped by Wolverine.
/// </summary>
public class nservicebus_postgresql_queue_externally_owned_5342
{
    private readonly string _queue = "nsb_ext_" + Guid.NewGuid().ToString("N");

    private async Task<bool> queueTableExistsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from information_schema.tables where table_schema = 'public' and table_name = @name";
        cmd.Parameters.AddWithValue("name", _queue);
        var count = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        await conn.CloseAsync();

        return count > 0;
    }

    private IHostBuilder owner() => Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbowner5342");
            opts.UseNServiceBusPostgresqlInterop(autoProvision: true);
            opts.ListenToNServiceBusPostgresqlQueue(_queue);

            opts.Discovery.DisableConventionalDiscovery();
        });

    private IHostBuilder consumer() => Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbconsumer5342");
            opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

            #region sample_nservicebus_postgresql_queue_is_externally_owned

            // The NServiceBus endpoint owns this queue table. Even with
            // autoProvision: true for the interop transport, Wolverine never
            // creates, migrates or drops it
            opts.PublishMessage<NsbInteropPing>()
                .ToNServiceBusPostgresqlQueue(_queue)
                .ExternallyOwned();

            #endregion

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.DisableConventionalLocalRouting();
        });

    [Fact]
    public async Task an_externally_owned_nservicebus_queue_is_never_created_or_dropped()
    {
        (await consumer().RunJasperFxCommands(["resources", "setup"])).ShouldBe(0);
        (await queueTableExistsAsync()).ShouldBeFalse();

        // NServiceBus, standing in here as the owner, creates the table
        (await owner().RunJasperFxCommands(["resources", "setup"])).ShouldBe(0);
        (await queueTableExistsAsync()).ShouldBeTrue();

        (await consumer().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);
        (await queueTableExistsAsync()).ShouldBeTrue();

        // The owner's own teardown still drops it
        (await owner().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);
        (await queueTableExistsAsync()).ShouldBeFalse();
    }
}
