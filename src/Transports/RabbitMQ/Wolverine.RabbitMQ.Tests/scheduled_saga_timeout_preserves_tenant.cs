using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class scheduled_saga_timeout_preserves_tenant : IAsyncLifetime
{
    private IHost _host = null!;
    private string _queueName = null!;

    public async Task InitializeAsync()
    {
        SagaTimeoutCapture.Reset();

        _queueName = RabbitTesting.NextQueueName();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "scheduled_saga_tenant";
                    m.Policies.AllDocumentsAreMultiTenanted();
                    m.AutoCreateSchemaObjects = AutoCreate.All;
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine()
                .UseLightweightSessions();

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.ListenToRabbitQueue(_queueName);
                opts.PublishMessage<SagaTimeoutMsg>().ToRabbitQueue(_queueName);

                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.AutoApplyTransactions();

                opts.Durability.ScheduledJobFirstExecution = 500.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task saga_timeout_delivered_through_rabbit_is_handled_under_original_tenant()
    {
        var sagaId = Guid.NewGuid();

        await _host.MessageBus().InvokeForTenantAsync("red", new StartSaga(sagaId));

        SagaTimeoutCapture.Snapshot captured;
        try
        {
            captured = await SagaTimeoutCapture.WaitAsync(30.Seconds());
        }
        catch (TimeoutException)
        {
            var store = _host.Services.GetRequiredService<IDocumentStore>();
            var snapshots = new List<string>();
            foreach (var tenant in new[] { "red", "*DEFAULT*" })
            {
                await using var diag = store.QuerySession(tenant);
                var row = await diag.LoadAsync<TenantedRabbitSaga>(sagaId);
                if (row != null)
                {
                    snapshots.Add($"tenant={tenant}, storedTenant={row.StoredTenantId ?? "null"}, timedOut={row.TimedOut}");
                }
            }
            throw new ShouldAssertException(
                $"The scheduled saga timeout never reached the saga handler within 30s — " +
                $"the TimeoutMessage was silently dropped because the saga lookup missed. " +
                $"Saga rows for id {sagaId}: " +
                (snapshots.Count > 0 ? string.Join(" | ", snapshots) : "none"));
        }

        captured.WasFound.ShouldBeTrue();
        captured.EnvelopeTenantId.ShouldBe("red");
        captured.EnvelopeSagaId.ShouldBe(sagaId.ToString());
        captured.ContextTenantId.ShouldBe("red");

        // After Handle ran and called MarkCompleted, Marten removes the saga row.
        var store2 = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store2.QuerySession();
        var remaining = await session.Query<TenantedRabbitSaga>()
            .Where(x => x.Id == sagaId)
            .ToListAsync();

        remaining.ShouldBeEmpty();
    }
}

public record StartSaga(Guid SagaId);

public record SagaTimeoutMsg(Guid SagaId) : TimeoutMessage(2.Seconds());

public class TenantedRabbitSaga : Saga
{
    public Guid Id { get; set; }
    public string? StoredTenantId { get; set; }
    public bool TimedOut { get; set; }

    public static (TenantedRabbitSaga, SagaTimeoutMsg) Start(StartSaga cmd, Envelope envelope)
    {
        return (
            new TenantedRabbitSaga { Id = cmd.SagaId, StoredTenantId = envelope.TenantId },
            new SagaTimeoutMsg(cmd.SagaId)
        );
    }

    public void Handle(SagaTimeoutMsg timeout, Envelope envelope, IMessageContext context)
    {
        TimedOut = true;
        SagaTimeoutCapture.Capture(envelope, context, wasFound: true);
        MarkCompleted();
    }
}

public static class SagaTimeoutCapture
{
    public record Snapshot(
        string? EnvelopeTenantId,
        string? EnvelopeSagaId,
        string? ContextTenantId,
        bool WasFound);

    private static TaskCompletionSource<Snapshot> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        _tcs = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Capture(Envelope envelope, IMessageContext context, bool wasFound)
    {
        _tcs.TrySetResult(new Snapshot(envelope.TenantId, envelope.SagaId, context.TenantId, wasFound));
    }

    public static Task<Snapshot> WaitAsync(TimeSpan timeout) => _tcs.Task.WaitAsync(timeout);
}
