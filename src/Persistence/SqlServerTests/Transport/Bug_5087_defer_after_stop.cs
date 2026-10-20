using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace SqlServerTests.Transport;

/// <summary>
/// The SQL Server twin of https://github.com/JasperFx/wolverine/issues/5087. ListeningAgent stops the listener
/// BEFORE it latches and drains the receiver, and the drain hands every envelope the receiver still holds
/// back through <see cref="IListener.DeferAsync"/>. That sent with the token StopAsync had just cancelled.
/// </summary>
public class Bug_5087_defer_after_stop
{
    private const string SchemaName = "gh5087";

    [Fact]
    public async Task an_envelope_deferred_after_stop_goes_back_on_the_queue()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName)
                    .AutoProvision();

                // Declared without a listener, so nothing but the test touches the queue
                opts.PublishAllMessages().ToSqlServerQueue("gh5087");
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var queue = runtime.Options.Transports.OfType<SqlServerTransport>().Single().Queues["gh5087"];

        await using var listener = new SqlServerQueueListener(queue, runtime, Substitute.For<IReceiver>());

        // The queue tables live in the transport's own schema, not the store schema dropped above
        await queue.PurgeAsync(NullLogger.Instance);

        await listener.StopAsync();

        var failure = await Record.ExceptionAsync(async () => await listener.DeferAsync(ObjectMother.Envelope()));
        failure.ShouldBeNull("DeferAsync sent with the token StopAsync cancelled");

        (await queue.CountAsync()).ShouldBe(1);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
