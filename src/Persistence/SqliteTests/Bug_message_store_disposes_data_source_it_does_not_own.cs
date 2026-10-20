using Weasel.Sqlite;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// MessageDatabase.DisposeAsync disposed its data source unconditionally, including one the caller passed to
/// PersistMessagesWithSqlite(DbDataSource). Wolverine now disposes only a data source it built itself.
/// </summary>
public class Bug_message_store_disposes_data_source_it_does_not_own : SqliteContext
{
    [Fact]
    public async Task stopping_the_host_does_not_dispose_a_caller_supplied_data_source()
    {
        using var database = Servers.CreateDatabase(nameof(Bug_message_store_disposes_data_source_it_does_not_own));
        using var dataSource = new DisposalTrackingDataSource(database.ConnectionString);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlite(dataSource);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        dataSource.WasDisposed.ShouldBeFalse();
    }

    private class DisposalTrackingDataSource(string connectionString) : SqliteDataSource(connectionString)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
