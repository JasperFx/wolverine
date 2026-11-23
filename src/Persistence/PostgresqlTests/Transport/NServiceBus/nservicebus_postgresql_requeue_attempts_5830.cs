using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport.NServiceBus;

namespace PostgresqlTests.Transport.NServiceBus;

// GH-5830. A requeued NServiceBus PostgreSQL queue message came back as attempt 1 every time -- the re-insert wrote no
// attempt count and the receive transaction's rollback leaves the row unchanged -- so an attempt-bounded policy such
// as Requeue(3).Then.MoveToErrorQueue() never reached its limit and requeued the message forever.
public class nservicebus_postgresql_requeue_attempts_5830 : IAsyncLifetime
{
    private const string Schema = "nsb5830";
    private readonly string _queueName = "nsb_attempts_" + Guid.NewGuid().ToString("N");
    private IHost _publisher = null!;

    public async ValueTask InitializeAsync()
    {
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                opts.PublishMessage<PgNsbAlwaysFails5830>().ToNServiceBusPostgresqlQueue(_queueName);

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
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
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
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                var listener = opts.ListenToNServiceBusPostgresqlQueue(_queueName).PollingInterval(100.Milliseconds());
                if (mode == "buffered") listener.BufferedInMemory();
                if (mode == "inline") listener.ProcessInline();
                if (mode == "inline with a receive transaction") listener.ProcessInline().UseReceiveTransaction();

                opts.OnException<PgNsbAlwaysFailsException5830>().Requeue(3).Then.MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<PgNsbAlwaysFails5830Handler>();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        await _publisher.MessageBus().SendAsync(new PgNsbAlwaysFails5830(run));

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (PgNsbAlwaysFails5830Handler.AttemptsFor(run).Length < 3)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message was never handled three times");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var id = PgNsbAlwaysFails5830Handler.MessageIdFor(run);

        // Dead lettered after the third attempt...
        deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (await scalarAsync($"select count(*) from {Schema}.wolverine_dead_letters where id = @id", id) == 0)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message was never dead lettered");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // ...and never handled again
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        PgNsbAlwaysFails5830Handler.AttemptsFor(run).ShouldBe([1, 2, 3]);
        var table = _publisher.Services.GetRequiredService<IWolverineRuntime>().Options.Transports
            .GetOrCreate<NServiceBusPostgresqlTransport>().Queues[_queueName].TableIdentifier.QualifiedName;
        (await scalarAsync($"select count(*) from {table}")).ShouldBe(0);

        await receiving.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record PgNsbAlwaysFails5830(Guid Run);

public class PgNsbAlwaysFailsException5830 : Exception;

public class PgNsbAlwaysFails5830Handler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<int>> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, Guid> _ids = new();

    public static int[] AttemptsFor(Guid run) => _attempts.TryGetValue(run, out var attempts) ? attempts.ToArray() : [];

    public static Guid MessageIdFor(Guid run) => _ids[run];

    public static void Handle(PgNsbAlwaysFails5830 message, Envelope envelope)
    {
        _ids[message.Run] = envelope.Id;
        _attempts.GetOrAdd(message.Run, _ => new ConcurrentQueue<int>()).Enqueue(envelope.Attempts);
        throw new PgNsbAlwaysFailsException5830();
    }
}
