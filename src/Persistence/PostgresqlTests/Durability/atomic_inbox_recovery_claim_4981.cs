using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace PostgresqlTests.Durability;

public class atomic_inbox_recovery_claim_4981
{
    private const string SchemaName = "inbox_recovery_claim_4981";
    private const string TableName = SchemaName + ".wolverine_incoming_envelopes";

    [Fact]
    public async Task concurrent_recovery_commands_enqueue_each_envelope_once()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.DurabilityAgentEnabled = false;
            opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
            opts.Durability.InboxStaleTime = TimeSpan.FromHours(1);
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        }).StartAsync(cancellation);

        var runtime = host.GetRuntime();
        var store = runtime.Storage;
        var endpoint = new LocalQueue("recovery-4981");
        var expected = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var enqueued = new ConcurrentBag<Envelope>();
        var circuit = Substitute.For<IListenerCircuit>();
        circuit.Endpoint.Returns(endpoint);
        circuit.Status.Returns(ListeningStatus.Accepting);
        circuit.EnqueueDirectlyAsync(Arg.Any<IEnumerable<Envelope>>()).Returns(call =>
        {
            foreach (var envelope in call.Arg<IEnumerable<Envelope>>()) enqueued.Add(envelope);
            return Task.CompletedTask;
        });

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);
        foreach (var id in expected)
        {
            await conn.CreateCommand($"""
                insert into {TableName} (id, status, owner_id, body, message_type, received_at, timestamp)
                values (@id, 'Incoming', 0, @body, 'test', @address, now() - interval '2 hours'),
                       (@id, 'Incoming', 0, @body, 'test', 'local://other', now() - interval '2 hours')
                """)
                .With("id", id).With("body", Array.Empty<byte>())
                .With("address", endpoint.Uri.ToString()).ExecuteNonQueryAsync(cancellation);
        }

        // Hold the first update until the competing command has reached the database.
        await conn.CreateCommand($"""
            create function {SchemaName}.hold_claim() returns trigger language plpgsql as $$
            begin
                perform pg_advisory_xact_lock(4981, 1);
                return NEW;
            end $$;
            create trigger hold_claim before update of owner_id on {TableName}
                for each row when (OLD.owner_id = 0 and NEW.owner_id <> 0)
                execute function {SchemaName}.hold_claim();
            select pg_advisory_lock(4981, 1);
            """).ExecuteNonQueryAsync(cancellation);

        Task? first = null;
        Task? second = null;
        try
        {
            var count = new IncomingCount(endpoint.Uri, expected.Length);
            first = new RecoverIncomingMessagesCommand(store, count, circuit, runtime.DurabilitySettings,
                NullLogger.Instance).ExecuteAsync(runtime, cancellation);
            await waitForBlockedClaims(1);

            second = new RecoverIncomingMessagesCommand(store, count, circuit, runtime.DurabilitySettings,
                NullLogger.Instance).ExecuteAsync(runtime, cancellation);
            await waitForBlockedClaims(2, second);
        }
        finally
        {
            await conn.CreateCommand("select pg_advisory_unlock(4981, 1)").ExecuteNonQueryAsync(cancellation);
            try
            {
                await Task.WhenAll(new[] { first, second }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(15), cancellation);
            }
            finally
            {
                await conn.CreateCommand($"""
                    drop trigger hold_claim on {TableName};
                    drop function {SchemaName}.hold_claim();
                    """).ExecuteNonQueryAsync(cancellation);
            }
        }

        enqueued.Select(x => x.Id).OrderBy(x => x).ShouldBe(expected.OrderBy(x => x));
        enqueued.ShouldAllBe(x => x.Store == store);
        (await conn.CreateCommand($"select owner_id from {TableName} where received_at = 'local://other'")
            .FetchListAsync<int>(cancellation)).ShouldAllBe(x => x == 0);
        (await conn.CreateCommand($"select timestamp > now() - interval '1 minute' from {TableName} where received_at = @address")
            .With("address", endpoint.Uri.ToString()).FetchListAsync<bool>(cancellation)).ShouldAllBe(x => x);
    }

    private static async Task waitForBlockedClaims(int count, Task? completed = null)
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (completed?.IsCompleted == true)
            {
                return;
            }

            var blocked = await conn.CreateCommand($"""
                select count(*) from pg_stat_activity
                where datname = current_database() and wait_event_type = 'Lock'
                  and query like '%{SchemaName}%'
                """).ExecuteScalarAsync(cancellation);
            if (Convert.ToInt64(blocked) >= count) return;
            await Task.Delay(10, cancellation);
        }

        throw new TimeoutException($"Expected {count} recovery commands blocked in PostgreSQL.");
    }
}
