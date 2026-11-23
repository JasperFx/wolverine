using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace PostgresqlTests.Sagas;

// GH-5839: completing a saga from outside its handlers, in the lightweight PostgreSQL saga tables
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<TerminableSaga>();
        opts.AddSagaType<RetainedTerminableSaga>();
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_termination_pg");
    }
}

// GH-5839: a saga a handler changes while an operator completes it is refused, not overwritten. The test holds the
// saga's row in an open transaction that bumps its version, so the completion has read the old version and then
// waits on the row; once the transaction commits, the completion's version check fails
public class saga_termination_concurrency_5839 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<TerminableSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AddSagaType<TerminableSaga>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_termination_pg");
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_saga_changed_underneath_the_completion_is_refused()
    {
        var id = Guid.NewGuid().ToString();
        await _host.InvokeMessageAndWaitAsync(new StartTerminableSaga(id));

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = "update saga_termination_pg.terminablesaga_saga set version = version + 1 where id = @id";
            bump.Parameters.AddWithValue("id", id);
            (await bump.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        var completion = Task.Run(() => _host.GetRuntime().SagaStorage.CompleteSagaAsync(
            typeof(TerminableSaga).FullName!, id, "stuck", TestContext.Current.CancellationToken));

        // The completion reads the committed (old) version, then waits on the row this transaction holds
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        completion.IsCompleted.ShouldBeFalse();

        await tx.CommitAsync(TestContext.Current.CancellationToken);

#pragma warning disable VSTHRD003 // The completion was started by this test, above
        await Should.ThrowAsync<SagaConcurrencyException>(() => completion);
#pragma warning restore VSTHRD003

        // Nothing was deleted
        (await _host.GetRuntime().SagaStorage.ReadSagaAsync(typeof(TerminableSaga).FullName!, id,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }
}
