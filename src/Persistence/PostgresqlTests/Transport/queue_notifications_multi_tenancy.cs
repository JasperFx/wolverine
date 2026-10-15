using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PostgresqlTests.MultiTenancy;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;
using Wolverine.Util;

namespace PostgresqlTests.Transport;

public class queue_notifications_multi_tenancy : MultiTenancyContext
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private string MainSchema => $"qnotif_mt_{_suffix}";
    private string TransportSchema => $"qnotif_mq_{_suffix}";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            })
            .EnableMessageTransport(transport =>
            {
                transport.TransportSchemaName(TransportSchema);
                transport.EnableQueueNotifications();
            });

        opts.Services.AddResourceSetupOnStartup();
        opts.ListenToPostgresqlQueue("tenant_messages");
        opts.PublishMessage<QueueNotificationProbe>().ToPostgresqlQueue("tenant_messages");
    }

    [Fact]
    public async Task creates_one_notification_connection_per_active_database_and_wakes_the_tenant_listener()
    {
        var runtime = theHost.GetRuntime();
        var appName = PostgresqlQueueNotification.ApplicationName(TransportSchema);

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = conn.CreateCommand();
            command.CommandText = "select count(*) from pg_stat_activity where application_name = @name";
            command.Parameters.AddWithValue("name", appName);
            Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(4);
        }

        var id = Guid.NewGuid();
        var received = QueueNotificationProbeHandler.WaitForAsync(id, 5.Seconds());
        var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["tenant_messages"];
        var serializer = runtime.Options.DefaultSerializer!;
        var envelope = new Envelope(new QueueNotificationProbe(id))
        {
            Id = id,
            TenantId = "red",
            Destination = queue.Uri,
            SentAt = DateTimeOffset.UtcNow,
            ContentType = serializer.ContentType,
            MessageType = typeof(QueueNotificationProbe).ToMessageTypeName()
        };
        envelope.Data = serializer.Write(envelope);

        await queue.SendAsync(envelope);
        await received;
    }
}
