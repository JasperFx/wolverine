using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Schema;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

namespace SqlServerTests.Persistence;

[Collection("marten")]
public class SqlServerMessageStore_DQL_expiration
{
    [Fact]
    public async Task no_expiration_column_normally()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "target");

                opts.ListenAtPort(2345).UseDurableInbox();
                opts.Durability.DeadLetterQueueExpirationEnabled = false;
            }).StartAsync();
        
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var runtime = host.GetRuntime();
        
        var dlq = await new DeadLettersTable(runtime.Options.Durability, "target").FetchExistingAsync(conn);
        dlq.ColumnFor(DatabaseConstants.Expires).ShouldBeNull();
    }

    [Fact]
    public async Task add_expiration_time_column_if_DLQ_expiration_is_enabled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dlq_expiration");

                opts.ListenAtPort(2345).UseDurableInbox();
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
            }).StartAsync();
        
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var runtime = host.GetRuntime();
        
        var dlq = await new DeadLettersTable(runtime.Options.Durability, "dlq_expiration").FetchExistingAsync(conn);
        var column = dlq.ColumnFor(DatabaseConstants.Expires);
        column.ShouldNotBeNull();
        column.AllowNulls.ShouldBeTrue();
    }
}