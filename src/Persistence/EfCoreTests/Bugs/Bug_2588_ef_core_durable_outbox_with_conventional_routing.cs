using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace EfCoreTests.Bugs;

/// <summary>
/// Reproducer for https://github.com/JasperFx/wolverine/issues/2588.
///
/// The reporter's setup mirrors a typical Wolverine app: EF Core DbContext
/// (manual envelope mapping), Postgres message persistence, RabbitMQ with
/// conventional routing, and Policies.UseDurableOutboxOnAllSendingEndpoints
/// (plus AutoApplyTransactions / UseDurableInboxOnAllListeners). Their HTTP
/// endpoint returns a tuple `(Response, CascadedEvent)`. They observe at
/// runtime that the cascading event bypasses the EF transaction / outbox —
/// `Mode == Inline` (the actual reporter saw `InlineSendingAgent`; this
/// repro shows the equivalent default `BufferedInMemory`, both meaning
/// "policy never applied").
///
/// The pre-existing Bug_2304 test exercises a similar policy expectation
/// against Marten + RabbitMQ and passes — but only because it never
/// registers the message handler with `IncludeType`. This reproducer
/// includes the handler, mirroring the reporter's real app.
///
/// The test does NOT exchange messages with RabbitMQ — it just inspects
/// the resolved sender endpoint Mode after `RoutingFor` is called.
/// </summary>
[Collection("postgresql")]
public class Bug_2588_ef_core_durable_outbox_with_conventional_routing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Faithful to reporter: EF Core with manual envelope mapping.
                opts.Services.AddDbContext<Bug2588DbContext>(o =>
                    o.UseNpgsql(Servers.PostgresConnectionString));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");
                opts.UseEntityFrameworkCoreTransactions();

                opts.UseRabbitMq()
                    .UseConventionalRouting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                opts.Durability.Mode = DurabilityMode.Solo;

                // Critical to reproduce: register the handler so conventional
                // routing's DiscoverListeners pre-creates the exchange via
                // ApplyListenerRoutingDefaults. That early creation makes
                // BrokerTransport.InitializeAsync compile the exchange BEFORE
                // any DiscoverSenders has added the subscription, so AllSenders
                // policies (UseDurableOutboxOnAllSendingEndpoints) never apply
                // — the _hasCompiled flag short-circuits a re-application
                // when DiscoverSenders later adds the subscription on first
                // publish.
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug2588Handler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void conventionally_routed_sender_should_be_durable_when_handler_is_also_registered()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var routes = runtime.RoutingFor(typeof(Bug2588Message))
            .ShouldBeOfType<MessageRouter<Bug2588Message>>()
            .Routes;

        routes.Length.ShouldBeGreaterThan(0);

        var route = routes.Single().ShouldBeOfType<MessageRoute>();
        var endpoint = route.Sender.Endpoint;

        // Reporter's symptom in unit-test form. With
        // UseDurableOutboxOnAllSendingEndpoints() the conventionally-routed
        // RabbitMQ exchange should have EndpointMode.Durable so cascading
        // messages participate in the outbox transaction. On main with the
        // handler registered, this comes back as BufferedInMemory because
        // the exchange was Compile()'d during BrokerTransport.InitializeAsync
        // (before DiscoverSenders ran) and the AllSenders policy gated on
        // `e.Subscriptions.Any()` short-circuited.
        endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }
}

public record Bug2588Message(Guid Id);

public class Bug2588Handler
{
    // Triggers conventional listener creation for Bug2588Message at startup,
    // which in turn calls RabbitMqMessageRoutingConvention.ApplyListenerRoutingDefaults
    // and pre-creates the sender exchange before AllSenders policies apply.
    public static void Handle(Bug2588Message _) { }
}

public class Bug2588DbContext(DbContextOptions<Bug2588DbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapWolverineEnvelopeStorage("wolverine");

        modelBuilder.Entity<Item>(map =>
        {
            map.ToTable("bug_2588_items");
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id");
            map.Property(x => x.Name).HasColumnName("name");
        });

        base.OnModelCreating(modelBuilder);
    }
}
