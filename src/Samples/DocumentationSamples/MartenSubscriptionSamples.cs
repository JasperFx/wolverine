using JasperFx.Core;
using Marten;
using Marten.Events;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Marten.Events.Daemon.Resiliency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Subscriptions;
using Wolverine.RabbitMQ;

namespace DocumentationSamples;

public class MartenSubscriptionSamples
{
    public static async Task subscribe_to_marten_events()
    {
        #region sample_publish_events_to_wolverine_subscribers

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten()

                    // Just pulling the connection information from
                    // the IoC container at runtime.
                    .UseNpgsqlDataSource()

                    // You don't absolutely have to have the Wolverine
                    // integration active here for subscriptions, but it's
                    // more than likely that you will want this anyway
                    .IntegrateWithWolverine()

                    // The Marten async daemon most be active
                    .AddAsyncDaemon(DaemonMode.HotCold)

                    // This would attempt to publish every non-archived event
                    // from Marten to Wolverine subscribers
                    .PublishEventsToWolverine("Everything")

                    // You wouldn't do this *and* the above option, but just to show
                    // the filtering
                    .PublishEventsToWolverine("Orders", relay =>
                    {
                        // Filtering
                        relay.FilterIncomingEventsOnStreamType(typeof(Order));

                        // Optionally, tell Marten to only subscribe to new
                        // events whenever this subscription is first activated
                        relay.Options.SubscribeFromPresent();
                    });
            }).StartAsync();

        #endregion
    }

    public static async Task invoke_marten_events_in_order()
    {
        #region sample_inline_invocation_of_wolverine_messages_for_marten_subscription

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(o =>
                    {
                        // This is the default setting, but just showing
                        // you that Wolverine subscriptions will be able
                        // to skip over messages that fail without
                        // shutting down the subscription
                        o.Projections.Errors.SkipApplyErrors = true;
                    })

                    // Just pulling the connection information from
                    // the IoC container at runtime.
                    .UseNpgsqlDataSource()

                    // You don't absolutely have to have the Wolverine
                    // integration active here for subscriptions, but it's
                    // more than likely that you will want this anyway
                    .IntegrateWithWolverine()

                    // The Marten async daemon most be active
                    .AddAsyncDaemon(DaemonMode.HotCold)

                    // Notice the allow list filtering of event types and the possibility of overriding
                    // the starting point for this subscription at runtime
                    .ProcessEventsWithWolverineHandlersInStrictOrder("Orders", o =>
                    {
                        // It's more important to create an allow list of event types that can be processed
                        o.IncludeType<OrderCreated>();

                        // Optionally mark the subscription as only starting from events from a certain time
                        o.Options.SubscribeFromTime(new DateTimeOffset(new DateTime(2023, 12, 1)));
                    });
            }).StartAsync();

        #endregion
    }

    public static async Task batched_subscription_usage()
    {
        #region sample_registering_a_batched_subscription

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();

                // There needs to be *some* kind of subscriber for CompanyActivations
                // for this to work at all
                opts.PublishMessage<CompanyActivations>()
                    .ToRabbitExchange("activations");

                opts.Services
                    .AddMarten()

                    // Just pulling the connection information from
                    // the IoC container at runtime.
                    .UseNpgsqlDataSource()

                    .IntegrateWithWolverine()

                    // The Marten async daemon most be active
                    .AddAsyncDaemon(DaemonMode.HotCold)


                    // Register the new subscription
                    .SubscribeToEvents(new CompanyTransferSubscription());
            }).StartAsync();

        #endregion
    }

    public static async Task batched_subscription_usage_with_services()
    {
        #region sample_registering_a_batched_subscription_with_services

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();

                // There needs to be *some* kind of subscriber for CompanyActivations
                // for this to work at all
                opts.PublishMessage<CompanyActivations>()
                    .ToRabbitExchange("activations");

                opts.Services
                    .AddMarten()

                    // Just pulling the connection information from
                    // the IoC container at runtime.
                    .UseNpgsqlDataSource()

                    .IntegrateWithWolverine()

                    // The Marten async daemon most be active
                    .AddAsyncDaemon(DaemonMode.HotCold)

                    // Register the new subscription
                    // With this alternative you can inject services into your subscription's constructor
                    // function
                    .SubscribeToEventsWithServices<CompanyTransferSubscription>(ServiceLifetime.Scoped);

            }).StartAsync();

        #endregion
    }
}

#region sample_transforming_event_to_external_integration_events

public record OrderCreated(string OrderNumber, Guid CustomerId);

// I wouldn't use this kind of suffix in real life, but it helps
// document *what* this is for the sample in the docs:)
public record OrderCreatedIntegrationEvent(string OrderNumber, string CustomerName, DateTimeOffset Timestamp);

// We're going to use the Marten IEvent metadata and some other Marten reference
// data to transform the internal OrderCreated event
// to an OrderCreatedIntegrationEvent that will be more appropriate for publishing to
// external systems
public static class InternalOrderCreatedHandler
{
    public static Task<Customer?> LoadAsync(IEvent<OrderCreated> e, IQuerySession session,
        CancellationToken cancellationToken)
        => session.LoadAsync<Customer>(e.Data.CustomerId, cancellationToken);


    public static OrderCreatedIntegrationEvent Handle(IEvent<OrderCreated> e, Customer customer)
    {
        return new OrderCreatedIntegrationEvent(e.Data.OrderNumber, customer.Name, e.Timestamp);
    }
}

#endregion


#region sample_CompanyTransferSubscriptions

public record CompanyActivated(string Name);

public record CompanyDeactivated();

public record NewCompany(Guid Id, string Name);

// Message type we're going to publish to external
// systems to keep them up to date on new companies
public class CompanyActivations
{
    public List<NewCompany> Additions { get; set; } = new();
    public List<Guid> Removals { get; set; } = new();

    public void Add(Guid companyId, string name)
    {
        Removals.Remove(companyId);

        // Fill is an extension method in JasperFx.Core that adds the
        // record to a list if the value does not already exist
        Additions.Fill(new NewCompany(companyId, name));
    }

    public void Remove(Guid companyId)
    {
        Removals.Fill(companyId);

        Additions.RemoveAll(x => x.Id == companyId);
    }
}

public class CompanyTransferSubscription : BatchSubscription
{
    public CompanyTransferSubscription() : base("CompanyTransfer")
    {
        IncludeType<CompanyActivated>();
        IncludeType<CompanyDeactivated>();
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        var activations = new CompanyActivations();
        foreach (var e in page.Events)
        {
            switch (e)
            {
                // In all cases, I'm assuming that the Marten stream id is the identifier for a customer
                case IEvent<CompanyActivated> activated:
                    activations.Add(activated.StreamId, activated.Data.Name);
                    break;
                case IEvent<CompanyDeactivated> deactivated:
                    activations.Remove(deactivated.StreamId);
                    break;
            }
        }

        // At the end of all of this, publish a single message
        // In case you're wondering, this will opt into Wolverine's
        // transactional outbox with the same transaction as any changes
        // made by Marten's IDocumentOperations passed in, including Marten's
        // own work to track the progression of this subscription
        await bus.PublishAsync(activations);
    }
}

#endregion
