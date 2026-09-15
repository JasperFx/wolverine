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
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

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

        var chain = graph.CodeFirstChains
            .Where(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService))
            .ShouldHaveSingleItem();
        chain.TypeName.ShouldBe("ExplicitlyRegisteredServiceGrpcHandler");
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
        GrpcGraph.FindCodeFirstServiceContracts([typeof(IExplicitlyRegisteredService).Assembly])
            .ShouldNotContain(typeof(IExplicitlyRegisteredService));
    }

    [Fact]
    public void include_code_first_contract_is_deduplicated()
    {
        var options = new WolverineGrpcOptions();

        options.IncludeCodeFirstContract<IExplicitlyRegisteredService>()
            .IncludeCodeFirstContract(typeof(IExplicitlyRegisteredService));

        options.CodeFirstContracts.ShouldBe([typeof(IExplicitlyRegisteredService)]);
    }

    [Fact]
    public void repeat_add_wolverine_grpc_calls_do_not_duplicate_the_registration()
    {
        var services = new ServiceCollection();
        services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IExplicitlyRegisteredService>());
        services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IExplicitlyRegisteredService>());

        var options = services.Single(d => d.ServiceType == typeof(WolverineGrpcOptions))
            .ImplementationInstance.ShouldBeOfType<WolverineGrpcOptions>();

        options.CodeFirstContracts.ShouldBe([typeof(IExplicitlyRegisteredService)]);
    }

    [Theory]
    [InlineData(typeof(NotAnInterfaceContract))]
    [InlineData(typeof(INotAServiceContract))]
    [InlineData(typeof(IInternalContract))]
    [InlineData(typeof(IGenericContract<>))]
    [InlineData(typeof(IGenericContract<ExplicitReply>))]
    public void rejects_a_type_that_cannot_be_a_code_first_contract(Type contractType)
    {
        var options = new WolverineGrpcOptions();

        var ex = Should.Throw<ArgumentException>(() => options.IncludeCodeFirstContract(contractType));

        ex.ParamName.ShouldBe("contractType");
        options.CodeFirstContracts.ShouldBeEmpty();
    }

    [Fact]
    public void discovery_builds_a_code_first_chain_for_a_registered_contract()
    {
        var graph = buildGraph();

        graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<IExplicitlyRegisteredService>());

        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService)).ShouldBe(1);
        graph.HandWrittenChains.ShouldNotContain(c => c.ServiceClassType == typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void without_registration_the_implementing_class_is_an_ordinary_hand_written_service()
    {
        var graph = buildGraph();

        graph.DiscoverServices(new WolverineGrpcOptions());

        graph.CodeFirstChains.ShouldNotContain(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService));
        graph.HandWrittenChains.ShouldContain(c => c.ServiceClassType == typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void registering_an_already_attributed_contract_does_not_produce_a_second_chain()
    {
        var graph = buildGraph();

        graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<ICodeFirstTestService>());

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

        WolverineGrpcExtensions.FindGrpcServiceTypes([typeof(ExplicitlyRegisteredEchoGrpcService).Assembly], graph)
            .ShouldNotContain(typeof(ExplicitlyRegisteredEchoGrpcService));
    }

    [Fact]
    public void discovery_refuses_a_registered_contract_whose_implementation_is_attributed()
    {
        var graph = buildGraph();
        var options = new WolverineGrpcOptions().IncludeCodeFirstContract<IConflictRegisteredContract>();

        var ex = Should.Throw<InvalidOperationException>(() => graph.DiscoverServices(options));

        ex.Message.ShouldContain(nameof(ConflictRegisteredImpl));
        ex.Message.ShouldContain(nameof(WolverineGrpcOptions.IncludeCodeFirstContract));
    }

    [Fact]
    public void the_service_registry_captures_a_registered_contract()
    {
        // TypeLoadMode.Static reads code-first contracts from this registry instead of scanning.
        var graph = buildGraph();
        graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<IExplicitlyRegisteredService>());

        var codeFile = graph.BuildFiles().OfType<GrpcServiceRegistryCodeFile>().Single();
        codeFile.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, graph.Container.Services);

        var registry = (GrpcServiceRegistry)Activator.CreateInstance(codeFile.RegistryType!)!;

        registry.CodeFirstContractTypes().ShouldContain(typeof(IExplicitlyRegisteredService));
        registry.HandWrittenServiceTypes().ShouldNotContain(typeof(ExplicitlyRegisteredEchoGrpcService));
    }

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

[Collection(GrpcSerialTestsCollection.Name)]
public class explicit_registration_static_mode_tests
{
    [Fact]
    public void a_registration_missing_from_the_pre_generated_registry_fails_discovery()
    {
        // An application assembly whose pre-generated registry predates the registration.
        var codegen = buildGraph(GetType().Assembly, TypeLoadMode.Auto);
        var registryFile = new GrpcServiceRegistryCodeFile([], [], [], []);
        registryFile.As<ICodeFile>().InitializeSynchronously(codegen.Rules, codegen, codegen.Container.Services);

        var graph = buildGraph(registryFile.RegistryType!.Assembly, TypeLoadMode.Static);

        Should.Throw<MissingPreBuiltTypesException>(() =>
            graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<IExplicitlyRegisteredService>()));

        graph.CodeFirstChains.ShouldContain(c => c.ServiceContractType == typeof(IExplicitlyRegisteredService));
    }

    private static GrpcGraph buildGraph(System.Reflection.Assembly applicationAssembly, TypeLoadMode mode)
    {
        var registry = new ServiceCollection();
        registry.AddLogging();
        registry.AddTransient<IServiceVariableSource>(c =>
            new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, JasperFx.RuntimeCompiler.AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        var options = new WolverineOptions { ApplicationAssembly = applicationAssembly };
        options.CodeGeneration.TypeLoadMode = mode;

        return new GrpcGraph(options, container);
    }
}
