using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
///     A handler or HTTP endpoint can take the shared <see cref="IEventStoreOperations" /> straight as a
///     parameter, on Marten, Polecat and Fisher alike. In every case it resolves to
///     <c>IDocumentSession.Events</c>.
/// </summary>
/// <remarks>
///     The assertion that matters is not "the parameter was non-null" — it is that appending through the
///     parameter lands in the database when the handler's transaction commits. That can only be true if the
///     parameter is the <b>current session's</b> Events rather than some other session's, which is what makes
///     this a real check on the variable source rather than a smoke test.
/// </remarks>
public class event_store_operations_parameter : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LedgerHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "event_store_ops_param";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_parameter_is_the_current_sessions_events()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new RecordLedgerEntry(id, "opening"));

        // Committed by the handler's own transaction, which only happens if the parameter was this
        // session's Events rather than a detached one
        await using var session = _host.DocumentStore().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<LedgerEntryRecorded>().Note.ShouldBe("opening");
    }
}

public record LedgerEntryRecorded(string Note);

public record RecordLedgerEntry(Guid Id, string Note);

[WolverineIgnore]
public static class LedgerHandler
{
    // The shared JasperFx contract, not Marten's own derived IEventStoreOperations -- the same signature
    // compiles and runs against Polecat and Fisher
    public static void Handle(RecordLedgerEntry command, IEventStoreOperations events)
    {
        events.StartStream(command.Id, new LedgerEntryRecorded(command.Note));
    }
}
