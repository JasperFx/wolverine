using CoreTests.Persistence.ByNamespace.Orders;
using CoreTests.Persistence.ByNamespace.Orders.Slices;
using CoreTests.Persistence.ByNamespace.OrdersArchive;
using CoreTests.Persistence.ByNamespace.Shipping;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Persistence
{
    // UseAncillaryStorageFromNamespace is the namespace-scoped counterpart of
    // UseAncillaryStorageFromAssembly, for modules that share an assembly. Reuses the stub frame
    // provider from ancillary_storage_by_assembly_policy.cs.

    public class ancillary_storage_by_namespace_policy
    {
        private static Task<IHost> hostFor(Action<WolverineOptions> configure)
        {
            return Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton<IAncillaryStoreFrameProvider, StubAncillaryStoreFrameProvider>();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<OrdersHandler>()
                        .IncludeType<OrdersSliceHandler>()
                        .IncludeType<OrdersAnnotatedHandler>()
                        .IncludeType<OrdersArchiveHandler>()
                        .IncludeType<ShippingHandler>();

                    configure(opts);
                }).StartAsync(TestContext.Current.CancellationToken);
        }

        private static HandlerChain chainFor<T>(IHost host)
        {
            return host.Services.GetRequiredService<HandlerGraph>().ChainFor<T>()!;
        }

        [Fact]
        public async Task routes_a_handler_in_the_named_namespace()
        {
            using var host = await hostFor(opts =>
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore)));

            chainFor<OrdersMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
        }

        [Fact]
        public async Task routes_a_handler_in_a_child_namespace()
        {
            using var host = await hostFor(opts =>
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore)));

            chainFor<OrdersSliceMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
        }

        [Fact]
        public async Task leaves_a_namespace_that_only_shares_a_prefix_alone()
        {
            using var host = await hostFor(opts =>
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore)));

            chainFor<OrdersArchiveMessage>(host).AncillaryStoreType.ShouldBeNull();
        }

        [Fact]
        public async Task leaves_a_sibling_namespace_alone()
        {
            using var host = await hostFor(opts =>
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore)));

            chainFor<ShippingMessage>(host).AncillaryStoreType.ShouldBeNull();
        }

        [Fact]
        public async Task routes_two_modules_in_one_assembly_to_different_stores()
        {
            using var host = await hostFor(opts =>
            {
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore));
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<ShippingHandler>(typeof(IOtherByAssemblyStore));
            });

            chainFor<OrdersMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
            chainFor<ShippingMessage>(host).AncillaryStoreType.ShouldBe(typeof(IOtherByAssemblyStore));
        }

        [Fact]
        public async Task an_explicit_attribute_beats_the_namespace_default()
        {
            using var host = await hostFor(opts =>
                opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore)));

            var annotated = chainFor<OrdersAnnotatedMessage>(host);
            annotated.AncillaryStoreType.ShouldBe(typeof(IOtherByAssemblyStore));
            annotated.Middleware.OfType<CommentFrame>().ShouldBeEmpty();
        }

        [Fact]
        public async Task the_assignment_is_visible_on_a_started_host()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton<IAncillaryStoreFrameProvider, StubAncillaryStoreFrameProvider>();
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<OrdersHandler>();
                    opts.Policies.UseAncillaryStorageFromNamespaceContaining<OrdersHandler>(typeof(IByAssemblyStore));
                }).StartAsync(TestContext.Current.CancellationToken);

            var chain = host.Services.GetRequiredService<HandlerGraph>()
                .HandlerFor<OrdersMessage>()!.As<MessageHandler>().Chain!;

            chain.AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
        }

        [Fact]
        public void a_namespace_is_required()
        {
            Should.Throw<ArgumentOutOfRangeException>(() =>
                new AncillaryStorageByNamespacePolicy(typeof(IByAssemblyStore), ""));
        }
    }
}

namespace CoreTests.Persistence.ByNamespace.Orders
{
    public record OrdersMessage(string Name);

    public record OrdersAnnotatedMessage(string Name);

    public class OrdersHandler
    {
        public static void Handle(OrdersMessage message)
        {
        }
    }

    public class OrdersAnnotatedHandler
    {
        [Storage(typeof(IOtherByAssemblyStore))]
        public static void Handle(OrdersAnnotatedMessage message)
        {
        }
    }
}

namespace CoreTests.Persistence.ByNamespace.Orders.Slices
{
    public record OrdersSliceMessage(string Name);

    public class OrdersSliceHandler
    {
        public static void Handle(OrdersSliceMessage message)
        {
        }
    }
}

namespace CoreTests.Persistence.ByNamespace.OrdersArchive
{
    public record OrdersArchiveMessage(string Name);

    public class OrdersArchiveHandler
    {
        public static void Handle(OrdersArchiveMessage message)
        {
        }
    }
}

namespace CoreTests.Persistence.ByNamespace.Shipping
{
    public record ShippingMessage(string Name);

    public class ShippingHandler
    {
        public static void Handle(ShippingMessage message)
        {
        }
    }
}
