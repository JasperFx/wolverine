using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests;

public class bumping_stale_inbox_messages : IAsyncLifetime
{
    private IHost theHost;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "stale_outbox");
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
    }

    [Fact]
    public async Task got_the_right_column()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var table = await new Table(new DbObjectName("stale_outbox", DatabaseConstants.IncomingTable)).FetchExistingAsync(conn);
        
        table.HasColumn(DatabaseConstants.Timestamp).ShouldBeTrue();
        
    }

    [Fact]
    public async Task smoke_test_on_the_persist()
    {
        var envelope = ObjectMother.Envelope();
        var messageStore = theHost.GetRuntime().Storage;
        await messageStore.Inbox.StoreIncomingAsync(envelope);
    }

    [Fact]
    public async Task using_the_operation()
    {
        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();
        var envelope3 = ObjectMother.Envelope();
        var envelope4 = ObjectMother.Envelope();
        var envelope5 = ObjectMother.Envelope();
        
        
        var messageStore = theHost.GetRuntime().Storage;
        await messageStore.Inbox.StoreIncomingAsync(envelope1);
        await messageStore.Inbox.StoreIncomingAsync(envelope2);
        await messageStore.Inbox.StoreIncomingAsync(envelope3);
        await messageStore.Inbox.StoreIncomingAsync(envelope4);
        await messageStore.Inbox.StoreIncomingAsync(envelope5);
        
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand("update stale_outbox.wolverine_incoming_envelopes set timestamp = @time where id = @id")
            .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
            .With("id", envelope1.Id)
            .ExecuteNonQueryAsync();
        
        await conn.CreateCommand("update stale_outbox.wolverine_incoming_envelopes set timestamp = @time where id = @id")
            .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
            .With("id", envelope3.Id)
            .ExecuteNonQueryAsync();
        
        await conn.CreateCommand("update stale_outbox.wolverine_incoming_envelopes set timestamp = @time where id = @id")
            .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
            .With("id", envelope5.Id)
            .ExecuteNonQueryAsync();
        
        var envelopesBefore = await messageStore.Admin.AllIncomingAsync();
        envelopesBefore.Count(x => x.OwnerId == 0).ShouldBe(0);

        var operation = new BumpStaleIncomingEnvelopesOperation(
            new DbObjectName("stale_outbox", DatabaseConstants.IncomingTable), theHost.GetRuntime().Options.Durability, DateTimeOffset.UtcNow);
        
        var batch = new DatabaseOperationBatch((IMessageDatabase)messageStore, [operation]);
        await theHost.InvokeAsync(batch);

        var envelopesAfter = await messageStore.Admin.AllIncomingAsync();
        envelopesAfter.Count(x => x.OwnerId == 0).ShouldBe(3);

    }
}