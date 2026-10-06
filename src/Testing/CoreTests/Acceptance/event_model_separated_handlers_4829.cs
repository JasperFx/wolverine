using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel4829
{
    // GH-4829: under MultipleHandlerBehavior.Separated, each module's handler of the same message is its
    // own sticky chain. Every one of them used to be named for the message, the name is the merge key,
    // and Merge folded them into ONE slice naming the first handler and claiming every module's output.
    public class event_model_separated_handlers_4829
    {
        private static async Task<IHost> hostAsync(Action<IServiceCollection>? services = null, params Type[] handlers)
        {
            return await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ServiceName = "Orders";
                    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                    var discovery = opts.Discovery.DisableConventionalDiscovery();
                    foreach (var handler in handlers) discovery.IncludeType(handler);

                    services?.Invoke(opts.Services);
                })
                .StartAsync();
        }

        private static async Task<EventModelDescriptor> assembleAsync(IHost host)
            => (await WolverineEventModelExport.AssembleSetAsync(host.Services)).Models.Single();

        [Fact]
        public async Task each_modules_handler_gets_its_own_slice_that_survives_the_merge()
        {
            // The issue's repro, verbatim: nothing declares a domain, so each slice is qualified by the
            // one thing that is unique per chain -- its handler type
            using var host = await hostAsync(null, typeof(Shipping.OrderPlacedHandler), typeof(Billing.OrderPlacedHandler));

            var derived = WolverineEventModelSource.Describe(host.Services.GetRequiredService<WolverineOptions>());
            var merged = EventModelDescriptor.Merge(derived.Name, new[] { derived.WithProvenance(EventModelProvenance.Derived) });

            var shipping = merged.Slices.Single(x => x.HandlerType?.FullName == typeof(Shipping.OrderPlacedHandler).FullName);
            shipping.Name.ShouldBe($"OrderPlaced ({typeof(Shipping.OrderPlacedHandler).FullName})");
            shipping.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(Shipping.ShipmentRequested) });

            var billing = merged.Slices.Single(x => x.HandlerType?.FullName == typeof(Billing.OrderPlacedHandler).FullName);
            billing.Name.ShouldBe($"OrderPlaced ({typeof(Billing.OrderPlacedHandler).FullName})");
            billing.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(Billing.InvoiceRequested) });

            merged.Slices.SelectMany(x => x.Hotspots).ShouldBeEmpty();
            shipping.Domain.ShouldBeNull();
            billing.Domain.ShouldBeNull();
        }

        [Fact]
        public async Task a_message_with_a_single_handler_keeps_its_bare_name()
        {
            using var host = await hostAsync(null, typeof(Shipping.OrderPlacedHandler));

            var model = await assembleAsync(host);
            model.Slices.Single().Name.ShouldBe(nameof(OrderPlaced));
        }

        [Fact]
        public async Task a_declared_domain_names_and_tags_each_modules_slice()
        {
            // Billing by a namespace policy on the declared model; Warehouse by [Domain] on the handler
            using var host = await hostAsync(
                services => services.AddEventModel("Orders", model =>
                    model.Domain("Billing").IncludesNamespaceOf<Billing.InvoiceRequested>()),
                typeof(Warehouse.OrderPlacedHandler), typeof(Billing.OrderPlacedHandler));

            var model = await assembleAsync(host);

            var billing = model.Slices.Single(x => x.Name == "OrderPlaced (Billing)");
            billing.Domain.ShouldBe("Billing");
            billing.HandlerType!.FullName.ShouldBe(typeof(Billing.OrderPlacedHandler).FullName);
            billing.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(Billing.InvoiceRequested) });

            var warehouse = model.Slices.Single(x => x.Name == "OrderPlaced (Warehouse)");
            warehouse.Domain.ShouldBe("Warehouse");
            warehouse.HandlerType!.FullName.ShouldBe(typeof(Warehouse.OrderPlacedHandler).FullName);
            warehouse.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(Warehouse.StockReserved) });
        }

        [Fact]
        public async Task one_emitter_links_to_every_consuming_slice()
        {
            using var host = await hostAsync(null,
                typeof(PlaceOrderHandler), typeof(Shipping.OrderPlacedHandler), typeof(Billing.OrderPlacedHandler));

            var model = await assembleAsync(host);

            var consumers = model.Links
                .Where(x => x.FromSlice == nameof(PlaceOrder) && x.Kind == EventModelLinkKind.MessageTriggers)
                .Select(x => x.ToSlice)
                .OrderBy(x => x)
                .ToArray();

            consumers.ShouldBe(new[]
            {
                $"OrderPlaced ({typeof(Billing.OrderPlacedHandler).FullName})",
                $"OrderPlaced ({typeof(Shipping.OrderPlacedHandler).FullName})"
            });

            // ...and the message renders once, as the one element both arrows leave from
            model.Slices.Single(x => x.Name == nameof(PlaceOrder)).PublishedMessages
                .Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlaced) });
        }

        [Fact]
        public async Task a_declaration_the_winner_overrode_is_called_out_on_the_slice()
        {
            // [Domain("Warehouse")] on the class beats a namespace policy putting it in Fulfillment -- the
            // attribute wins, but the disagreement is a hotspot rather than lost
            using var host = await hostAsync(
                services => services.AddEventModel("Orders", model =>
                    model.Domain("Fulfillment").IncludesNamespaceOf<Warehouse.StockReserved>()),
                typeof(Warehouse.OrderPlacedHandler), typeof(Billing.OrderPlacedHandler));

            var model = await assembleAsync(host);

            var warehouse = model.Slices.Single(x => x.Name == "OrderPlaced (Warehouse)");
            warehouse.Domain.ShouldBe("Warehouse");
            warehouse.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.Prose && x.Text.Contains("'Fulfillment'"));
        }
    }

    public record OrderPlaced(Guid OrderId);

    public record PlaceOrder(Guid OrderId);

    public class PlaceOrderHandler
    {
        public OrderPlaced Handle(PlaceOrder command) => new(command.OrderId);
    }
}

namespace CoreTests.Acceptance.EventModel4829.Shipping
{
    public record ShipmentRequested(Guid OrderId);

    public class OrderPlacedHandler
    {
        public ShipmentRequested Handle(OrderPlaced e) => new(e.OrderId);
    }
}

namespace CoreTests.Acceptance.EventModel4829.Warehouse
{
    public record StockReserved(Guid OrderId);

    [Domain("Warehouse")]
    public class OrderPlacedHandler
    {
        public StockReserved Handle(OrderPlaced e) => new(e.OrderId);
    }
}

namespace CoreTests.Acceptance.EventModel4829.Billing
{
    public record InvoiceRequested(Guid OrderId);

    public class OrderPlacedHandler
    {
        public InvoiceRequested Handle(OrderPlaced e) => new(e.OrderId);
    }
}
