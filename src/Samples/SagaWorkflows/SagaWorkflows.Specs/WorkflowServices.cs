using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaWorkflows.MoneyTransfer;
using SagaWorkflows.MoneyTransfer.Fraud;
using SagaWorkflows.OrderApproval;
using SagaWorkflows.Subscriptions;
using SagaWorkflows.TripBooking;
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
                opts.Services.AddSingleton<TravelProviders>();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(BankHandler))
                    .IncludeType(typeof(StorefrontHandler))
                    .IncludeType(typeof(TravelProviderHandler));

                opts.ListenAtPort(ProvidersPort).UseDurableInbox();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.PublishMessage<MoneyWithdrawn>().ToPort(WorkflowsPort);
                opts.PublishMessage<MoneyDeposited>().ToPort(WorkflowsPort);
                opts.PublishMessage<TransferRecorded>().ToPort(WorkflowsPort);
                opts.PublishMessage<TransferScreened>().ToPort(WorkflowsPort);
                opts.PublishMessage<PaymentProcessed>().ToPort(WorkflowsPort);
                opts.PublishMessage<FlightBooked>().ToPort(WorkflowsPort);
                opts.PublishMessage<CarBooked>().ToPort(WorkflowsPort);
                opts.PublishMessage<HotelBooked>().ToPort(WorkflowsPort);

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

                // A saga's timeouts are kept in the durable store, where a superseded one can be deleted
                opts.Policies.UseDurableLocalQueues();

                // The bank's steps, one rule per message type
                opts.PublishMessage<Withdraw>().ToPort(ProvidersPort);
                opts.PublishMessage<Deposit>().ToPort(ProvidersPort);
                opts.PublishMessage<RecordTransfer>().ToPort(ProvidersPort);
                opts.PublishMessage<RefundWithdrawal>().ToPort(ProvidersPort);
                opts.PublishMessage<ReverseDeposit>().ToPort(ProvidersPort);
                opts.PublishMessage<TransferCompleted>().ToPort(ProvidersPort);
                opts.PublishMessage<TransferFailed>().ToPort(ProvidersPort);

                // The storefront and the billing provider
                opts.PublishMessage<RequestApproval>().ToPort(ProvidersPort);
                opts.PublishMessage<ProcessPayment>().ToPort(ProvidersPort);
                opts.PublishMessage<NotifyCustomer>().ToPort(ProvidersPort);
                opts.PublishMessage<ChargeCustomer>().ToPort(ProvidersPort);
                opts.PublishMessage<SubscriptionEnded>().ToPort(ProvidersPort);

                // The travel providers
                opts.PublishMessage<BookFlight>().ToPort(ProvidersPort);
                opts.PublishMessage<BookCar>().ToPort(ProvidersPort);
                opts.PublishMessage<BookHotel>().ToPort(ProvidersPort);
                opts.PublishMessage<CancelFlight>().ToPort(ProvidersPort);
                opts.PublishMessage<CancelCar>().ToPort(ProvidersPort);
                opts.PublishMessage<CancelHotel>().ToPort(ProvidersPort);
                opts.PublishMessage<TripBooked>().ToPort(ProvidersPort);

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
        Providers.Services.GetRequiredService<TravelProviders>().Reset();
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
        => opts.Discovery.IncludeType(typeof(Transfer)).IncludeType(typeof(Order)).IncludeType(typeof(Subscription))
            .IncludeType(typeof(Trip));
}

/// <summary>The same workflows as event-sourced sagas, on the same messages.</summary>
public class EventSourcedSagaServices : WorkflowServices
{
    protected override string Schema => "saga_workflows_es";

    protected override void ConfigureWorkflows(WolverineOptions opts)
        => opts.Discovery.IncludeType(typeof(EventSourcedTransfer)).IncludeType(typeof(EventSourcedSubscription))
            .IncludeType(typeof(EventSourcedTrip));
}

[CollectionDefinition(nameof(DocumentSagaCollection))]
public class DocumentSagaCollection : ICollectionFixture<DocumentSagaServices>;

[CollectionDefinition(nameof(EventSourcedSagaCollection))]
public class EventSourcedSagaCollection : ICollectionFixture<EventSourcedSagaServices>;
