using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaWorkflows.MoneyTransfer;
using SagaWorkflows.MoneyTransfer.Fraud;
using SagaWorkflows.Providers;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace SagaWorkflows.Specs;

/// <summary>
/// Two Wolverine services in one test process, talking over TCP: "workflows", where the sagas live, and "providers",
/// the outside world they drive. Each has its own Marten store (a schema of its own) and its own durable inbox and
/// outbox, so a step that fails in the providers service is dead-lettered there and its fault comes back.
/// </summary>
public abstract class WorkflowServices : IAsyncLifetime
{
    public const string WorkflowsName = "workflows";
    public const string ProvidersName = "providers";

    public IHost Workflows { get; private set; } = null!;
    public IHost Providers { get; private set; } = null!;

    public int WorkflowsPort { get; } = PortFinder.GetAvailablePort();
    public int ProvidersPort { get; } = PortFinder.GetAvailablePort();

    /// <summary>A schema prefix of the fixture's own, so the fixtures' sagas and dead letters never meet.</summary>
    protected abstract string Schema { get; }

    /// <summary>The sagas this fixture's workflows service has.</summary>
    protected abstract void ConfigureWorkflows(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Providers = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = ProvidersName;
                useMarten(opts, $"{Schema}_providers");

                opts.Services.AddSingleton<Bank>();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(BankHandler));

                opts.ListenAtPort(ProvidersPort).UseDurableInbox();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.PublishMessage<MoneyWithdrawn>().ToPort(WorkflowsPort);
                opts.PublishMessage<MoneyDeposited>().ToPort(WorkflowsPort);
                opts.PublishMessage<TransferRecorded>().ToPort(WorkflowsPort);
                opts.PublishMessage<TransferScreened>().ToPort(WorkflowsPort);

                // Every failure is retried twice, so three attempts, then dead-lettered. A terminal failure is not retried
                opts.OnException<Exception>().RetryTimes(2);
            })
            .StartAsync();

        Workflows = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = WorkflowsName;
                useMarten(opts, $"{Schema}_workflows");

                opts.Discovery.DisableConventionalDiscovery();

                opts.ListenAtPort(WorkflowsPort).UseDurableInbox();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                // The bank's steps, one rule per message type
                opts.PublishMessage<Withdraw>().ToPort(ProvidersPort);
                opts.PublishMessage<Deposit>().ToPort(ProvidersPort);
                opts.PublishMessage<RecordTransfer>().ToPort(ProvidersPort);
                opts.PublishMessage<RefundWithdrawal>().ToPort(ProvidersPort);
                opts.PublishMessage<ReverseDeposit>().ToPort(ProvidersPort);
                opts.PublishMessage<TransferCompleted>().ToPort(ProvidersPort);
                opts.PublishMessage<TransferFailed>().ToPort(ProvidersPort);

                // Fraud screening, by namespace: this rule names no message type
                opts.Publish(x =>
                {
                    x.MessagesFromNamespaceContaining<ScreenTransfer>();
                    x.ToPort(ProvidersPort);
                });

                opts.OnException<Exception>().RetryTimes(2);

                ConfigureWorkflows(opts);
            })
            .StartAsync();

        await ResetAsync();
    }

    private static void useMarten(WolverineOptions opts, string schema)
    {
        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
                m.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine();

        opts.Policies.AutoApplyTransactions();
    }

    /// <summary>Every specification starts from empty stores and a bank with no accounts.</summary>
    public async Task ResetAsync()
    {
        await Workflows.ResetAllMartenDataAsync();
        await Providers.ResetAllMartenDataAsync();

        // Nothing a specification left in an inbox, outbox or dead letter table reaches the next one
        await Workflows.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.ClearAllAsync();
        await Providers.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.ClearAllAsync();

        Providers.Services.GetRequiredService<Bank>().Reset();
    }

    public async ValueTask DisposeAsync()
    {
        await Workflows.StopAsync();
        await Providers.StopAsync();
        Workflows.Dispose();
        Providers.Dispose();
    }
}

/// <summary>The workflows as document sagas.</summary>
public class DocumentSagaServices : WorkflowServices
{
    protected override string Schema => "saga_workflows";

    protected override void ConfigureWorkflows(WolverineOptions opts)
        => opts.Discovery.IncludeType(typeof(Transfer));
}

/// <summary>The same workflows as event-sourced sagas, on the same messages.</summary>
public class EventSourcedSagaServices : WorkflowServices
{
    protected override string Schema => "saga_workflows_es";

    protected override void ConfigureWorkflows(WolverineOptions opts)
        => opts.Discovery.IncludeType(typeof(EventSourcedTransfer));
}

[CollectionDefinition(nameof(DocumentSagaCollection))]
public class DocumentSagaCollection : ICollectionFixture<DocumentSagaServices>;

[CollectionDefinition(nameof(EventSourcedSagaCollection))]
public class EventSourcedSagaCollection : ICollectionFixture<EventSourcedSagaServices>;
