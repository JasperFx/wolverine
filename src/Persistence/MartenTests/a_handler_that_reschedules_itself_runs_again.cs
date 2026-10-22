using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

/// <summary>
/// F13 (durable compliance rig). A handler on a durable endpoint calls <c>ReScheduleAsync</c>, which hands the inbox
/// row back as Scheduled, and then returns normally. Two completions used to overwrite that row with Handled and lose
/// the rescheduled run: the durable receiver's own mark after the handler, and -- when the handler also saved
/// something -- the mark the Marten middleware queues into the handler's transaction.
/// </summary>
public class a_handler_that_reschedules_itself_runs_again : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        ReschedulingHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReschedulingHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "reschedules_itself";
                }).IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_rescheduled_run_happens(bool savesADocument)
    {
        var id = Guid.NewGuid();
        await _host.MessageBus().PublishAsync(new RescheduleMyself(id, savesADocument));

        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (ReschedulingHandler.RunsOf(id) < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        ReschedulingHandler.RunsOf(id).ShouldBe(2);
    }
}

public record RescheduleMyself(Guid Id, bool SavesADocument);

public class RescheduledDoc
{
    public Guid Id { get; set; }
    public int Runs { get; set; }
}

public static class ReschedulingHandler
{
    private static readonly LightweightCache<Guid, int> _runs = new(_ => 0);
    private static readonly object _lock = new();

    public static void Reset()
    {
        lock (_lock) _runs.Clear();
    }

    public static int RunsOf(Guid id)
    {
        lock (_lock) return _runs.Contains(id) ? _runs[id] : 0;
    }

    public static async Task Handle(RescheduleMyself message, IDocumentSession session, IMessageContext context)
    {
        int runs;
        lock (_lock) runs = ++_runs[message.Id];

        if (message.SavesADocument)
        {
            session.Store(new RescheduledDoc { Id = message.Id, Runs = runs });
        }

        // The first run hands the message back for later, then returns normally
        if (runs == 1)
        {
            await ((IEnvelopeLifecycle)context).ReScheduleAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        }
    }
}
