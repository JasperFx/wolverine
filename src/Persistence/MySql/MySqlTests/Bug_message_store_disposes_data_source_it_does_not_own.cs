using IntegrationTests;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;

namespace MySqlTests;

/// <summary>
/// MessageDatabase.DisposeAsync disposed its data source unconditionally, including one the caller passed to
/// PersistMessagesWithMySql(MySqlDataSource). Wolverine now disposes only a data source it built itself.
/// </summary>
[Collection("mysql")]
public class Bug_message_store_disposes_data_source_it_does_not_own
{
    [Fact]
    public async Task stopping_the_host_does_not_dispose_a_caller_supplied_data_source()
    {
        await using var dataSource = new MySqlDataSourceBuilder(Servers.MySqlConnectionString).Build();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithMySql(dataSource, "ds_ownership");
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        await using var command = dataSource.CreateCommand("select 1");
        Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }
}
