using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.MySql.Transport;

namespace MySqlTests.MultiTenancy;

/// <summary>
/// GH-4862. On a multi-tenanted host the queue tables are built per tenant database (GH-3859), and the
/// deterministic ordering option has to reach every one of them.
/// </summary>
[Collection("mysql")]
public class queue_ordering_in_tenant_databases : MySqlMultiTenancyContext
{
    private const string SchemaName = "queue_order_tenants";
    private const string QueueName = "orderedtenant";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName)
            .EnableMessageTransport(x => x.EnableDeterministicQueueOrdering())
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        opts.PublishAllMessages().ToMySqlQueue(QueueName);

        opts.Services.AddResourceSetupOnStartup();
    }

    [Fact]
    public async Task every_tenant_database_gets_the_ordering_column()
    {
        foreach (var connectionString in new[]
                 {
                     Servers.MySqlConnectionString, tenant1ConnectionString, tenant2ConnectionString,
                     tenant3ConnectionString
                 })
        {
            var database = new MySqlConnectionStringBuilder(connectionString).Database;

            foreach (var table in new[] { $"wolverine_queue_{QueueName}", $"wolverine_queue_{QueueName}_scheduled" })
            {
                await using var conn = new MySqlConnection(connectionString);
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                                  SELECT count(*) FROM information_schema.columns
                                  WHERE table_schema = @schema AND table_name = @table AND column_name = 'queue_order'
                                  """;
                cmd.Parameters.AddWithValue("@schema", database);
                cmd.Parameters.AddWithValue("@table", table);

                Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))
                    .ShouldBe(1L, $"Expected {database}.{table} to have the queue_order column");
            }
        }

        var queue = theHost.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>()
            .Options.Transports.GetOrCreate<MySqlTransport>().Queues[QueueName];
        (await queue.CheckAsync()).ShouldBeTrue();
    }
}
