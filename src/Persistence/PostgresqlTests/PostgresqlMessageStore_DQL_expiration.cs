using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Postgresql.Schema;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

namespace PostgresqlTests;

[Collection("marten")]
public class PostgresqlMessageStore_DQL_expiration
{
    [Fact]
    public async Task no_expiration_column_normally()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "dlq_expiration";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2345).UseDurableInbox();
                opts.Durability.DeadLetterQueueExpirationEnabled = false;
            }).StartAsync();
        
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var runtime = host.GetRuntime();
        
        var dlq = await new DeadLettersTable(runtime.Options.Durability, "receiver").FetchExistingAsync(conn);
        dlq.ColumnFor(DatabaseConstants.Expires).ShouldBeNull();
    }

    [Fact]
    public async Task add_expiration_time_column_if_DLQ_expiration_is_enabled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "dlq_expiration";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2345).UseDurableInbox();
                opts.Durability.DeadLetterQueueExpirationEnabled = true;

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var runtime = host.GetRuntime();
        
        var dlq = await new DeadLettersTable(runtime.Options.Durability, "dlq_expiration").FetchExistingAsync(conn);
        var column = dlq.ColumnFor(DatabaseConstants.Expires);
        column.ShouldNotBeNull();
        column.AllowNulls.ShouldBeTrue();
    }
}