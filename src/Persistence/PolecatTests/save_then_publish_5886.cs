using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.SqlClient;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Util;
using Wolverine.Transports.Tcp;

namespace PolecatTests;

/// <summary>
/// GH-5886. A transactional handler that commits its own session and then publishes. When the handler had already
/// published something before its own commit, that commit flushed the context, the chain's commit stores the message
/// published afterward, and the end of the pipeline has to send it. The durability agent is off, so the only thing
/// that can deliver the message is that flush.
/// </summary>
public class save_then_publish_5886 : IAsyncLifetime
{
    private const string SchemaName = "save_then_publish";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        SaveThenPublishCounts.Reset();

        var port = PortFinder.GetAvailablePort();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SaveThenPublishHandlers));

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = SchemaName;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.ListenAtPort(port).UseDurableInbox();
                opts.PublishMessage<SaveThenPublishExternalTarget>().ToPort(port).UseDurableOutbox();
                opts.PublishMessage<SaveThenPublishLocalTarget>().ToLocalQueue("save-then-publish").UseDurableInbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_message_published_only_after_the_handlers_own_save_is_sent()
    {
        var message = new SaveThenPublishToLocalQueue(Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        tracked.Executed.SingleMessage<SaveThenPublishLocalTarget>().Id.ShouldBe(message.Id);
        SaveThenPublishCounts.Deliveries(message.Id).ShouldBe(1);
    }

    [Fact]
    public async Task messages_published_to_a_durable_outbox_around_the_handlers_own_save_are_each_sent_once()
    {
        var message = new PublishToOutboxAroundTheSave(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected().IncludeExternalTransports()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        SaveThenPublishCounts.Deliveries(message.BeforeId).ShouldBe(1);
        SaveThenPublishCounts.Deliveries(message.AfterId).ShouldBe(1);

        // The row the chain's commit wrote for the later message is not left behind in the outbox

        await waitForNoOutgoingRowsAsync();
    }

    [Fact]
    public async Task messages_published_to_a_durable_local_queue_around_the_handlers_own_save_are_each_sent_once()
    {
        var message = new PublishAroundTheSave(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message);

        tracked.AllExceptions().ShouldBeEmpty();
        SaveThenPublishCounts.Deliveries(message.BeforeId).ShouldBe(1);
        SaveThenPublishCounts.Deliveries(message.AfterId).ShouldBe(1);
    }

    private async Task waitForNoOutgoingRowsAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(Token);

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        long rows;
        do
        {
            await using var count = conn.CreateCommand();
            count.CommandText = $"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}";
            rows = Convert.ToInt64(await count.ExecuteScalarAsync(Token));
            if (rows == 0) return;
            await Task.Delay(100.Milliseconds(), Token);
        } while (DateTimeOffset.UtcNow < deadline);

        rows.ShouldBe(0);
    }
}

public record SaveThenPublishToLocalQueue(Guid Id);

public record PublishToOutboxAroundTheSave(Guid BeforeId, Guid AfterId);

public record PublishAroundTheSave(Guid BeforeId, Guid AfterId);

public record SaveThenPublishLocalTarget(Guid Id);

public record SaveThenPublishExternalTarget(Guid Id);

public class SaveThenPublishDoc
{
    public Guid Id { get; set; }
}

public static class SaveThenPublishHandlers
{
    #region sample_polecat_committing_the_session_yourself
    [Transactional]
    public static async Task Handle(SaveThenPublishToLocalQueue message, IDocumentSession session, IMessageBus bus)
    {
        session.Store(new SaveThenPublishDoc { Id = message.Id });

        // The handler commits its own session...
        await session.SaveChangesAsync();

        // ...and then publishes. Wolverine still sends this message once the handler is done
        await bus.PublishAsync(new SaveThenPublishLocalTarget(message.Id));
    }
    #endregion

    [Transactional]
    public static async Task Handle(PublishToOutboxAroundTheSave message, IDocumentSession session, IMessageBus bus)
    {
        await bus.PublishAsync(new SaveThenPublishExternalTarget(message.BeforeId));

        session.Store(new SaveThenPublishDoc { Id = message.BeforeId });
        await session.SaveChangesAsync();

        await bus.PublishAsync(new SaveThenPublishExternalTarget(message.AfterId));
    }

    [Transactional]
    public static async Task Handle(PublishAroundTheSave message, IDocumentSession session, IMessageBus bus)
    {
        await bus.PublishAsync(new SaveThenPublishLocalTarget(message.BeforeId));

        session.Store(new SaveThenPublishDoc { Id = message.BeforeId });
        await session.SaveChangesAsync();

        await bus.PublishAsync(new SaveThenPublishLocalTarget(message.AfterId));
    }

    public static void Handle(SaveThenPublishLocalTarget message) => SaveThenPublishCounts.Delivered(message.Id);

    public static void Handle(SaveThenPublishExternalTarget message) => SaveThenPublishCounts.Delivered(message.Id);
}

public static class SaveThenPublishCounts
{
    private static readonly ConcurrentDictionary<Guid, int> _deliveries = new();

    public static void Reset() => _deliveries.Clear();

    public static void Delivered(Guid id) => _deliveries.AddOrUpdate(id, 1, (_, n) => n + 1);

    public static int Deliveries(Guid id) => _deliveries.GetValueOrDefault(id);
}
