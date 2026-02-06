using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Transports.Tcp;

namespace MySqlTests;

[Collection("mysql")]
public class MySqlMessageStoreTests : MessageStoreCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        // First, create and migrate the store directly without the node agent trying to access it
        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "receiver",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var durabilitySettings = new DurabilitySettings();
        var store = new MySqlMessageStore(settings, durabilitySettings, dataSource,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MySqlMessageStore>.Instance);

        await store.Admin.MigrateAsync();

        // Now start the host - schema already exists
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "receiver");
                opts.ListenAtPort(2345).UseDurableInbox();
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        var hostStore = host.Get<IMessageStore>();
        await hostStore.Admin.ClearAllAsync();

        return host;
    }

    [Fact]
    public async Task can_persist_and_delete_outgoing_envelope()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Outbox.StoreOutgoingAsync(envelope, 1);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Outgoing.ShouldBeGreaterThanOrEqualTo(1);

        await thePersistence.Outbox.DeleteOutgoingAsync([envelope]);

        var counts2 = await thePersistence.Admin.FetchCountsAsync();
        counts2.Outgoing.ShouldBe(counts.Outgoing - 1);
    }

    [Fact]
    public async Task can_move_envelope_to_dead_letter_queue()
    {
        var envelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var exception = new InvalidOperationException("Test error");
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.DeadLetter.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task can_mark_envelope_as_handled()
    {
        var envelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Handled.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task can_schedule_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduleDelay = 1.Hours();
        envelope.Status = EnvelopeStatus.Scheduled;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBeGreaterThanOrEqualTo(1);
    }
}
