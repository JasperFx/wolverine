using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport.NServiceBus;

namespace SqlServerTests.Transport.NServiceBus;

// GH-5830. A requeued NServiceBus SQL Server queue message came back as attempt 1 every time -- the re-insert wrote no
// attempt count and the receive transaction's rollback leaves the row unchanged -- so an attempt-bounded policy such
// as Requeue(3).Then.MoveToErrorQueue() never reached its limit and requeued the message forever.
public class nservicebus_sql_server_requeue_attempts_5830 : IAsyncLifetime
{
    private const string Schema = "nsb5830";
    private readonly string _queueName = "nsb_attempts_" + Guid.NewGuid().ToString("N");
    private IHost _publisher = null!;

    public async ValueTask InitializeAsync()
    {
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, Schema);
                opts.UseNServiceBusSqlServerInterop(autoProvision: true);

                opts.PublishMessage<NsbAlwaysFails5830>().ToNServiceBusSqlServerQueue(_queueName);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.StopAsync();
        _publisher.Dispose();
    }

    private static async Task<long> scalarAsync(string sql, Guid? id = null)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        if (id.HasValue) cmd.Parameters.AddWithValue("id", id.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("buffered")]
    [InlineData("inline")]
    [InlineData("inline with a receive transaction")]
    public async Task requeue_stops_at_its_attempt_limit_and_dead_letters_the_message(string mode)
    {
        var run = Guid.NewGuid();

        using var receiving = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, Schema);
                opts.UseNServiceBusSqlServerInterop(autoProvision: true);

                var listener = opts.ListenToNServiceBusSqlServerQueue(_queueName).PollingInterval(100.Milliseconds());
                if (mode == "buffered") listener.BufferedInMemory();
                if (mode == "inline") listener.ProcessInline();
                if (mode == "inline with a receive transaction") listener.ProcessInline().UseReceiveTransaction();

                opts.OnException<NsbAlwaysFailsException5830>().Requeue(3).Then.MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NsbAlwaysFails5830Handler>();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await _publisher.MessageBus().SendAsync(new NsbAlwaysFails5830(run));

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (NsbAlwaysFails5830Handler.AttemptsFor(run).Length < 3)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message was never handled three times");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var id = NsbAlwaysFails5830Handler.MessageIdFor(run);

        // Dead lettered after the third attempt...
        deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (await scalarAsync($"select count(*) from {Schema}.wolverine_dead_letters where id = @id", id) == 0)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message was never dead lettered");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // ...and never handled again
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        NsbAlwaysFails5830Handler.AttemptsFor(run).ShouldBe([1, 2, 3]);
        var table = _publisher.Services.GetRequiredService<IWolverineRuntime>().Options.Transports
            .GetOrCreate<NServiceBusSqlServerTransport>().Queues[_queueName].TableIdentifier.QualifiedName;
        (await scalarAsync($"select count(*) from {table}")).ShouldBe(0);

        await receiving.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record NsbAlwaysFails5830(Guid Run);

public class NsbAlwaysFailsException5830 : Exception;

public class NsbAlwaysFails5830Handler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<int>> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, Guid> _ids = new();

    public static int[] AttemptsFor(Guid run) => _attempts.TryGetValue(run, out var attempts) ? attempts.ToArray() : [];

    public static Guid MessageIdFor(Guid run) => _ids[run];

    public static void Handle(NsbAlwaysFails5830 message, Envelope envelope)
    {
        _ids[message.Run] = envelope.Id;
        _attempts.GetOrAdd(message.Run, _ => new ConcurrentQueue<int>()).Enqueue(envelope.Attempts);
        throw new NsbAlwaysFailsException5830();
    }
}

// GH-5830. A rolled back receive leaves the row unchanged, so the listener remembers the attempt count by message id
public class database_listener_remembers_rolled_back_attempts_5830
{
    private static async Task<(SqlConnection, System.Data.Common.DbTransaction)> openTransactionAsync()
    {
        var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return (conn, await conn.BeginTransactionAsync());
    }

    [Fact]
    public async Task a_rolled_back_message_comes_back_with_its_attempts_and_a_completed_one_is_forgotten()
    {
        await using var listener = new AttemptsListener5830();
        var id = Guid.NewGuid();

        var (conn, tx) = await openTransactionAsync();
        var first = new Envelope { Id = id, Attempts = 2 };
        listener.Hold(first, conn, tx);
        (await listener.RollbackAsync(first)).ShouldBeTrue();

        // The same row popped again maps to a fresh envelope
        (conn, tx) = await openTransactionAsync();
        var second = new Envelope { Id = id };
        listener.Hold(second, conn, tx);
        second.Attempts.ShouldBe(2);

        (await listener.CommitAsync(second)).ShouldBeTrue();
        listener.RememberedAttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task the_oldest_remembered_count_is_forgotten_past_the_limit()
    {
        await using var listener = new AttemptsListener5830 { RememberedAttemptsLimit = 2 };
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        foreach (var id in ids)
        {
            var (conn, tx) = await openTransactionAsync();
            var envelope = new Envelope { Id = id, Attempts = 1 };
            listener.Hold(envelope, conn, tx);
            await listener.RollbackAsync(envelope);
        }

        listener.RememberedAttemptCount.ShouldBe(2);

        var (c, t) = await openTransactionAsync();
        var oldest = new Envelope { Id = ids[0] };
        listener.Hold(oldest, c, t);
        oldest.Attempts.ShouldBe(0);
        await listener.RollbackAsync(oldest);

        (c, t) = await openTransactionAsync();
        var newest = new Envelope { Id = ids[2] };
        listener.Hold(newest, c, t);
        newest.Attempts.ShouldBe(1);
        await listener.RollbackAsync(newest);
    }
}

internal class AttemptsListener5830()
    : Wolverine.RDBMS.Transport.DatabaseListener(new Uri("test://attempts"), "attempts", new GatedReceiver5815(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.FromSeconds(1))
{
    protected override int MaximumMessagesToReceive => 1;

    protected override Task<IReadOnlyList<Envelope>> TryPopAsync(int maximumMessages, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<Envelope>>([]);

    public override ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public override ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public void Hold(Envelope envelope, System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction) =>
        holdReceive(envelope, connection, transaction);

    public ValueTask<bool> CommitAsync(Envelope envelope) => commitReceiveAsync(envelope);

    public ValueTask<bool> RollbackAsync(Envelope envelope) => rollbackReceiveAsync(envelope);
}
