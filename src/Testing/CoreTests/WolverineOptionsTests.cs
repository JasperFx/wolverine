﻿using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Oakton.Resources;
using TestingSupport;
using TestingSupport.Fakes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests;

public class WolverineOptionsTests
{
    private readonly WolverineOptions theSettings = new();

    [Fact]
    public void unique_node_id_is_really_unique()
    {
        var options1 = new DurabilitySettings();
        var options2 = new DurabilitySettings();
        var options3 = new DurabilitySettings();
        var options4 = new DurabilitySettings();
        var options5 = new DurabilitySettings();
        var options6 = new DurabilitySettings();

        options1.AssignedNodeNumber.ShouldNotBe(options2.AssignedNodeNumber);
        options1.AssignedNodeNumber.ShouldNotBe(options3.AssignedNodeNumber);
        options1.AssignedNodeNumber.ShouldNotBe(options4.AssignedNodeNumber);
        options1.AssignedNodeNumber.ShouldNotBe(options5.AssignedNodeNumber);
        options1.AssignedNodeNumber.ShouldNotBe(options6.AssignedNodeNumber);

        options2.AssignedNodeNumber.ShouldNotBe(options3.AssignedNodeNumber);
        options2.AssignedNodeNumber.ShouldNotBe(options4.AssignedNodeNumber);
        options2.AssignedNodeNumber.ShouldNotBe(options5.AssignedNodeNumber);
        options2.AssignedNodeNumber.ShouldNotBe(options6.AssignedNodeNumber);

        options3.AssignedNodeNumber.ShouldNotBe(options4.AssignedNodeNumber);
        options3.AssignedNodeNumber.ShouldNotBe(options5.AssignedNodeNumber);
        options3.AssignedNodeNumber.ShouldNotBe(options6.AssignedNodeNumber);

        options4.AssignedNodeNumber.ShouldNotBe(options5.AssignedNodeNumber);
        options4.AssignedNodeNumber.ShouldNotBe(options6.AssignedNodeNumber);

        options5.AssignedNodeNumber.ShouldNotBe(options6.AssignedNodeNumber);
    }

    [Fact]
    public async Task durable_local_queue_is_indeed_durable()
    {
        using var runtime = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        runtime.Services.GetRequiredService<IWolverineRuntime>()
            .Endpoints.EndpointFor(TransportConstants.DurableLocalUri)
            .Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public void sets_up_the_container_with_services()
    {
        using var runtime = WolverineHost.For(registry =>
        {
            registry.DisableConventionalDiscovery();
            registry.Services.AddScoped<IFoo, Foo>();
            registry.Services.AddSingleton<TestingSupport.Fakes.Tracking>();
            registry.Services.AddTransient<IFakeStore, FakeStore>();
        });

        var services = runtime.Get<IServiceContainer>();
        services.DefaultFor<IFoo>().ImplementationType.ShouldBe(typeof(Foo));
    }

    [Fact]
    public void stub_out_external_setting_via_IEndpoints()
    {
        var options = new WolverineOptions();
        options.ExternalTransportsAreStubbed.ShouldBeFalse();

        options.StubAllExternalTransports();

        options.ExternalTransportsAreStubbed.ShouldBeTrue();
    }

    [Fact]
    public void add_transport()
    {
        var transport = Substitute.For<ITransport>();
        transport.Protocol.Returns("fake");

        var collection = new WolverineOptions();
        collection.Transports.Add(transport);

        collection.Transports.ShouldContain(transport);
    }

    [Fact]
    public void try_to_get_endpoint_from_invalid_transport()
    {
        var collection = new WolverineOptions();
        Exception<InvalidOperationException>.ShouldBeThrownBy(() =>
        {
            collection.Transports.TryGetEndpoint("wrong://server".ToUri());
        });
    }

    [Fact]
    public void local_is_registered_by_default()
    {
        new WolverineOptions().Transports
            .OfType<LocalTransport>()
            .Count().ShouldBe(1);
    }

    [Fact]
    public void retrieve_transport_by_scheme()
    {
        new WolverineOptions().Transports
            .ForScheme("local")
            .ShouldBeOfType<LocalTransport>();
    }

    [Fact]
    public void retrieve_transport_by_type()
    {
        new WolverineOptions().Transports
            .GetOrCreate<LocalTransport>()
            .ShouldNotBeNull();
    }

    [Fact]
    public void all_endpoints()
    {
        var collection = new WolverineOptions();
        collection.ListenForMessagesFrom("stub://one");
        collection.PublishAllMessages().To("stub://two");

        // 2 default local queues + the 2 added here
        collection.Transports.AllEndpoints()
            .Length.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void publish_mechanism_with_multiple_subscribers()
    {
        var collection = new WolverineOptions();
        collection.Publish(x =>
        {
            x.MessagesFromNamespace("One");
            x.MessagesFromNamespace("Two");

            x.To("stub://3333");
            x.To("stub://4444");
        });

        var endpoint3333 = collection.Transports.TryGetEndpoint("stub://3333".ToUri());
        var endpoint4444 = collection.Transports.TryGetEndpoint("stub://4444".ToUri());

        endpoint3333.Subscriptions[0]
            .ShouldBe(new Subscription { Scope = RoutingScope.Namespace, Match = "One" });

        endpoint3333.Subscriptions[1]
            .ShouldBe(new Subscription { Scope = RoutingScope.Namespace, Match = "Two" });

        endpoint4444.Subscriptions[0]
            .ShouldBe(new Subscription { Scope = RoutingScope.Namespace, Match = "One" });

        endpoint4444.Subscriptions[1]
            .ShouldBe(new Subscription { Scope = RoutingScope.Namespace, Match = "Two" });
    }

    [Fact]
    public void create_transport_type_if_missing()
    {
        var collection = new WolverineOptions();
        var transport = collection.Transports.GetOrCreate<FakeTransport>();

        collection.Transports.GetOrCreate<FakeTransport>()
            .ShouldBeSameAs(transport);
    }

    public interface IFoo;

    public class Foo : IFoo;

    public class FakeTransport : ITransport
    {
        public string Name => "Fake";

        public string Protocol => "fake";


        public Endpoint ReplyEndpoint()
        {
            throw new NotImplementedException();
        }

        public Endpoint GetOrCreateEndpoint(Uri uri)
        {
            throw new NotImplementedException();
        }

        public Endpoint? TryGetEndpoint(Uri uri)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Endpoint> Endpoints()
        {
            throw new NotImplementedException();
        }

        public ValueTask InitializeAsync(IWolverineRuntime runtime)
        {
            throw new NotImplementedException();
        }

        public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource resource)
        {
            resource = null;
            return false;
        }

        public Endpoint ListenTo(Uri uri)
        {
            throw new NotImplementedException();
        }

        public void StartSenders(IWolverineRuntime root)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public ISendingAgent BuildSendingAgent(Uri uri, IWolverineRuntime root, CancellationToken cancellation)
        {
            throw new NotImplementedException();
        }

        public ISender CreateSender(Uri uri, CancellationToken cancellation, IWolverineRuntime root)
        {
            throw new NotImplementedException();
        }
    }
}