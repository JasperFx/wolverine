using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Grpc.Tests.CodeFirstCodegen;
using Xunit;

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

/// <summary>
///     GH-4396, end to end: a contract that carries only <c>[ServiceContract]</c>, registered through
///     <c>WolverineGrpcOptions.IncludeCodeFirstContract</c>, is served by a generated implementation
///     through <c>MapWolverineGrpcServices</c> exactly as an attributed contract would be.
/// </summary>
[Collection("grpc-explicit-registration")]
public class explicit_registration_integration_tests : IClassFixture<ExplicitRegistrationFixture>
{
    private readonly ExplicitRegistrationFixture _fixture;

    public explicit_registration_integration_tests(ExplicitRegistrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task round_trip_unary_through_the_generated_implementation()
    {
        var client = _fixture.CreateClient<IExplicitlyRegisteredService>();

        var reply = await client.Echo(new ExplicitRequest { Text = "hello" });

        // The handler's prefix, not the hand-written class's: the generated implementation answered.
        reply.Echo.ShouldBe("registered:hello");
    }

    [Fact]
    public async Task round_trip_server_streaming_through_the_generated_implementation()
    {
        var client = _fixture.CreateClient<IExplicitlyRegisteredService>();

        var replies = new List<string>();
        await foreach (var reply in client.EchoStream(new ExplicitStreamRequest { Text = "s", Count = 3 }))
        {
            replies.Add(reply.Echo);
        }

        replies.ShouldBe(["s:0", "s:1", "s:2"]);
    }

    [Fact]
    public void the_registered_contract_has_exactly_one_code_first_chain()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();

        // The fixture calls AddWolverineGrpc(configure) twice, so the callback ran twice.
        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService)).ShouldBe(1);

        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService));
        chain.TypeName.ShouldBe("ExplicitlyRegisteredServiceGrpcHandler");
        chain.GeneratedType.ShouldNotBeNull();
        typeof(IExplicitlyRegisteredService).IsAssignableFrom(chain.GeneratedType).ShouldBeTrue();
    }

    [Fact]
    public void the_implementing_grpc_service_class_is_not_also_wrapped_as_hand_written()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();

        graph.HandWrittenChains.ShouldNotContain(c => c.ServiceClassType == typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void each_rpc_of_the_registered_contract_is_routed_exactly_once()
    {
        // The double-mapping guard proper. Were ExplicitlyRegisteredEchoGrpcService wrapped or
        // direct-mapped alongside the generated implementation, both would answer the same route and
        // ASP.NET would report an ambiguous match on the first call.
        var rpcs = _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<GrpcMethodMetadata>())
            .Where(m => m != null && typeof(IExplicitlyRegisteredService).IsAssignableFrom(m.ServiceType))
            .Select(m => (m!.Method.Name, m.ServiceType))
            .ToList();

        rpcs.Select(x => x.Name).OrderBy(x => x).ShouldBe([
            nameof(IExplicitlyRegisteredService.Echo),
            nameof(IExplicitlyRegisteredService.EchoStream)
        ]);
        rpcs.ShouldAllBe(x => x.ServiceType != typeof(ExplicitlyRegisteredEchoGrpcService));
    }
}

[Collection("grpc-explicit-registration-unit")]
public class explicit_registration_unit_tests
{
    [Fact]
    public void the_attribute_scan_does_not_find_the_unattributed_contract()
    {
        // Baseline for everything below: without registration this contract is invisible.
        typeof(IExplicitlyRegisteredService).IsDefined(typeof(WolverineGrpcServiceAttribute), false).ShouldBeFalse();

        GrpcGraph.FindCodeFirstServiceContracts([typeof(IExplicitlyRegisteredService).Assembly])
            .ShouldNotContain(typeof(IExplicitlyRegisteredService));
    }

    [Fact]
    public void include_code_first_contract_is_deduplicated()
    {
        var options = new WolverineGrpcOptions();

        options.IncludeCodeFirstContract<IExplicitlyRegisteredService>()
            .IncludeCodeFirstContract(typeof(IExplicitlyRegisteredService))
            .IncludeCodeFirstContract<IExplicitlyRegisteredService>();

        options.CodeFirstContracts.ShouldBe([typeof(IExplicitlyRegisteredService)]);
    }

    [Fact]
    public void include_code_first_contract_preserves_registration_order()
    {
        var options = new WolverineGrpcOptions();

        options.IncludeCodeFirstContract<IConflictRegisteredContract>()
            .IncludeCodeFirstContract<IExplicitlyRegisteredService>();

        options.CodeFirstContracts.ShouldBe([typeof(IConflictRegisteredContract), typeof(IExplicitlyRegisteredService)]);
    }

    [Fact]
    public void repeat_add_wolverine_grpc_calls_do_not_duplicate_the_registration()
    {
        // AddWolverineGrpc(configure) re-runs the callback against the same singleton on every call.
        var services = new ServiceCollection();
        services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IExplicitlyRegisteredService>());
        services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IExplicitlyRegisteredService>());

        var options = services.Single(d => d.ServiceType == typeof(WolverineGrpcOptions))
            .ImplementationInstance.ShouldBeOfType<WolverineGrpcOptions>();

        options.CodeFirstContracts.ShouldBe([typeof(IExplicitlyRegisteredService)]);
    }

    [Fact]
    public void rejects_a_null_contract_type()
    {
        Should.Throw<ArgumentNullException>(() => new WolverineGrpcOptions().IncludeCodeFirstContract(null!));
    }

    [Fact]
    public void rejects_a_type_that_is_not_an_interface()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            new WolverineGrpcOptions().IncludeCodeFirstContract<NotAnInterfaceContract>());

        ex.Message.ShouldContain(nameof(NotAnInterfaceContract));
        ex.Message.ShouldContain("not an interface");
    }

    [Fact]
    public void rejects_an_open_generic_interface()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            new WolverineGrpcOptions().IncludeCodeFirstContract(typeof(IOpenGenericContract<>)));

        ex.Message.ShouldContain("IOpenGenericContract");
        ex.Message.ShouldContain("open generic");
    }

    [Fact]
    public void rejects_an_interface_without_service_contract()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            new WolverineGrpcOptions().IncludeCodeFirstContract<INotAServiceContract>());

        ex.Message.ShouldContain(nameof(INotAServiceContract));
        ex.Message.ShouldContain("[System.ServiceModel.ServiceContract]");
    }

    [Fact]
    public void a_rejected_registration_leaves_the_list_untouched()
    {
        var options = new WolverineGrpcOptions();

        Should.Throw<ArgumentException>(() => options.IncludeCodeFirstContract<INotAServiceContract>());

        options.CodeFirstContracts.ShouldBeEmpty();
    }

    [Fact]
    public void discovery_builds_a_code_first_chain_for_a_registered_contract()
    {
        var graph = buildGraph();
        var options = new WolverineGrpcOptions().IncludeCodeFirstContract<IExplicitlyRegisteredService>();

        graph.DiscoverServices(options);

        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService)).ShouldBe(1);
        graph.HandWrittenChains.ShouldNotContain(c => c.ServiceClassType == typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void without_registration_the_implementing_class_is_an_ordinary_hand_written_service()
    {
        // Proves the exclusion above is driven by the registration and not by anything about the class.
        var graph = buildGraph();

        graph.DiscoverServices(new WolverineGrpcOptions());

        graph.CodeFirstChains.ShouldNotContain(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService));
        graph.HandWrittenChains.ShouldContain(c => c.ServiceClassType == typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void registering_an_already_attributed_contract_does_not_produce_a_second_chain()
    {
        var graph = buildGraph();
        var options = new WolverineGrpcOptions().IncludeCodeFirstContract<ICodeFirstTestService>();

        graph.DiscoverServices(options);

        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(ICodeFirstTestService)).ShouldBe(1);
    }

    [Fact]
    public void find_hand_written_service_classes_excludes_implementations_of_registered_contracts()
    {
        var assemblies = new[] { typeof(ExplicitlyRegisteredEchoGrpcService).Assembly };

        GrpcGraph.FindHandWrittenServiceClasses(assemblies)
            .ShouldContain(typeof(ExplicitlyRegisteredEchoGrpcService));

        GrpcGraph.FindHandWrittenServiceClasses(assemblies, [typeof(IExplicitlyRegisteredService)])
            .ShouldNotContain(typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void direct_mapping_skips_implementations_of_registered_contracts()
    {
        var graph = buildGraph();
        graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<IExplicitlyRegisteredService>());

        // Not wrapped (see above) and not direct-mapped either: the contract already has a service.
        WolverineGrpcExtensions.FindGrpcServiceTypes([typeof(ExplicitlyRegisteredEchoGrpcService).Assembly], graph)
            .ShouldNotContain(typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void conflict_guard_fires_for_a_registered_contract_with_an_attributed_implementation()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            CodeFirstGrpcServiceChain.AssertNoConcreteImplementationConflicts(
                typeof(IConflictRegisteredContract),
                [typeof(IConflictRegisteredContract).Assembly],
                [typeof(IConflictRegisteredContract)]));

        ex.Message.ShouldContain(nameof(ConflictRegisteredImpl));
        // The diagnostic must name the registration, not claim the interface is attributed.
        ex.Message.ShouldContain(nameof(WolverineGrpcOptions.IncludeCodeFirstContract));
        ex.Message.ShouldNotContain("is marked [WolverineGrpcService]");
    }

    [Fact]
    public void conflict_guard_still_names_the_attribute_for_an_attributed_contract()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            CodeFirstGrpcServiceChain.AssertNoConcreteImplementationConflicts(
                typeof(IConflictingService),
                [typeof(IConflictingService).Assembly],
                []));

        ex.Message.ShouldContain("is marked [WolverineGrpcService]");
    }

    [Fact]
    public void conflict_guard_is_quiet_when_the_contract_is_not_registered()
    {
        Should.NotThrow(() =>
            CodeFirstGrpcServiceChain.AssertNoConcreteImplementationConflicts(
                typeof(IExplicitlyRegisteredService),
                [typeof(IExplicitlyRegisteredService).Assembly],
                [typeof(IExplicitlyRegisteredService)]));
    }

    [Fact]
    public void discovery_refuses_a_registered_contract_whose_implementation_is_attributed()
    {
        var graph = buildGraph();
        var options = new WolverineGrpcOptions().IncludeCodeFirstContract<IConflictRegisteredContract>();

        var ex = Should.Throw<InvalidOperationException>(() => graph.DiscoverServices(options));

        ex.Message.ShouldContain(nameof(ConflictRegisteredImpl));
    }

    [Fact]
    public void static_mode_registry_round_trips_a_registered_contract()
    {
        // TypeLoadMode.Static rebuilds chains from the pre-generated registry, which is projected from
        // the chains discovery built. A registered contract therefore needs nothing extra in Static
        // mode as long as it lands in that registry: emit -> compile -> read it back.
        var graph = buildGraph();
        graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<IExplicitlyRegisteredService>());

        var codeFile = graph.BuildFiles().OfType<GrpcServiceRegistryCodeFile>().Single();
        codeFile.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, graph.Container.Services);

        var registry = (GrpcServiceRegistry)Activator.CreateInstance(codeFile.RegistryType!)!;

        registry.CodeFirstContractTypes().ShouldContain(typeof(IExplicitlyRegisteredService));
        registry.HandWrittenServiceTypes().ShouldNotContain(typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    // Same minimal codegen harness as Bug_4156_static_mode_service_types / grpc_direct_mapped_manifest.
    private static GrpcGraph buildGraph()
    {
        var registry = new ServiceCollection();
        registry.AddLogging();
        registry.AddTransient<IServiceVariableSource>(c =>
            new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, JasperFx.RuntimeCompiler.AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        var options = new WolverineOptions { ApplicationAssembly = typeof(explicit_registration_unit_tests).Assembly };
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

        return new GrpcGraph(options, container);
    }
}
