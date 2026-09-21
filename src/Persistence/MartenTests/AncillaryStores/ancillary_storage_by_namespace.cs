using IntegrationTests;
using Marten;
using MartenTests.AncillaryStores.ByNamespace.Orders;
using MartenTests.AncillaryStores.ByNamespace.Shipping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores
{
    // Two modules in one assembly, each routed to its own ancillary store by namespace. The handlers
    // carry no storage attribute.
    public class ancillary_storage_by_namespace : IAsyncLifetime
    {
        private IHost theHost = null!;

        public async ValueTask InitializeAsync()
        {
            theHost = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.MessageStorageSchemaName = "wolverine";
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Policies.AutoApplyTransactions();

                    opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "by_namespace_main";
                        m.Events.DatabaseSchemaName = "by_namespace_main";
                    }).IntegrateWithWolverine();

                    opts.Services.AddMartenStore<IOrdersModuleStore>(m =>
                        {
                            m.Connection(Servers.PostgresConnectionString);
                            m.DatabaseSchemaName = "by_namespace_orders";
                            m.Events.DatabaseSchemaName = "by_namespace_orders";
                        })
                        .IntegrateWithWolverine();

                    opts.Services.AddMartenStore<IShippingModuleStore>(m =>
                        {
                            m.Connection(Servers.PostgresConnectionString);
                            m.DatabaseSchemaName = "by_namespace_shipping";
                            m.Events.DatabaseSchemaName = "by_namespace_shipping";
                        })
                        .IntegrateWithWolverine();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(OrdersModuleHandler))
                        .IncludeType(typeof(ShippingModuleHandler));

                    #region sample_use_ancillary_storage_from_namespace
                    // Two modules share one assembly, so each is scoped by its namespace instead.
                    // Child namespaces are included.
                    opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersModuleMessage>(
                        typeof(IOrdersModuleStore));
                    opts.Policies.UseAncillaryStorageFromNamespace(typeof(IShippingModuleStore),
                        "MartenTests.AncillaryStores.ByNamespace.Shipping");
                    #endregion

                    opts.Services.AddResourceSetupOnStartup();
                }).StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }

        [Fact]
        public async Task each_module_commits_through_its_own_store()
        {
            var order = new OrdersModuleMessage(Guid.NewGuid().ToString());
            var shipment = new ShippingModuleMessage(Guid.NewGuid().ToString());
            await theHost.InvokeMessageAndWaitAsync(order);
            await theHost.InvokeMessageAndWaitAsync(shipment);

            (await load<IOrdersModuleStore>(order.Id)).ShouldNotBeNull();
            (await load<IShippingModuleStore>(order.Id)).ShouldBeNull();

            (await load<IShippingModuleStore>(shipment.Id)).ShouldNotBeNull();
            (await load<IOrdersModuleStore>(shipment.Id)).ShouldBeNull();

            var mainStore = theHost.Services.GetRequiredService<IDocumentStore>();
            await using var mainSession = mainStore.QuerySession();
            (await mainSession.LoadAsync<ByNamespaceRecord>(order.Id, TestContext.Current.CancellationToken))
                .ShouldBeNull();
            (await mainSession.LoadAsync<ByNamespaceRecord>(shipment.Id, TestContext.Current.CancellationToken))
                .ShouldBeNull();
        }

        private async Task<ByNamespaceRecord?> load<T>(string id) where T : class, IDocumentStore
        {
            await using var session = theHost.DocumentStore<T>().QuerySession();
            return await session.LoadAsync<ByNamespaceRecord>(id, TestContext.Current.CancellationToken);
        }
    }

    public interface IOrdersModuleStore : IDocumentStore;

    public interface IShippingModuleStore : IDocumentStore;

    public class ByNamespaceRecord
    {
        public string Id { get; set; } = null!;
    }
}

namespace MartenTests.AncillaryStores.ByNamespace.Orders
{
    public record OrdersModuleMessage(string Id);

    public static class OrdersModuleHandler
    {
        public static void Handle(OrdersModuleMessage message, IDocumentSession session)
        {
            session.Store(new ByNamespaceRecord { Id = message.Id });
        }
    }
}

namespace MartenTests.AncillaryStores.ByNamespace.Shipping
{
    public record ShippingModuleMessage(string Id);

    public static class ShippingModuleHandler
    {
        public static void Handle(ShippingModuleMessage message, IDocumentSession session)
        {
            session.Store(new ByNamespaceRecord { Id = message.Id });
        }
    }
}
