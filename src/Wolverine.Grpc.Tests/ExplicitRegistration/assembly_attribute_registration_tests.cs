using GreeterProtoFirstGrpc.Server;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

/// <summary>
///     GH-4396, option 2: <c>[assembly: WolverineGrpcCodeFirstContract&lt;T&gt;]</c> in a scanned assembly
///     registers a <c>[ServiceContract]</c>-only contract exactly as <c>IncludeCodeFirstContract</c> does.
///     The attribute lives in <c>IAssemblyRegisteredService.cs</c>.
/// </summary>
[Collection("grpc-explicit-registration")]
public class assembly_attribute_registration_integration_tests : IClassFixture<ExplicitRegistrationFixture>
{
    private readonly ExplicitRegistrationFixture _fixture;

    public assembly_attribute_registration_integration_tests(ExplicitRegistrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task round_trip_unary_through_the_generated_implementation()
    {
        var client = _fixture.CreateClient<IAssemblyRegisteredService>();

        var reply = await client.Echo(new AssemblyRegisteredRequest { Text = "hi" });

        reply.Echo.ShouldBe("assembly:hi");
    }

    [Fact]
    public void the_attribute_registered_contract_has_exactly_one_code_first_chain()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();

        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(IAssemblyRegisteredService)).ShouldBe(1);
        graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(IAssemblyRegisteredService))
            .TypeName.ShouldBe("AssemblyRegisteredServiceGrpcHandler");
    }
}

[Collection("grpc-explicit-registration-unit")]
public class assembly_attribute_registration_unit_tests
{
    [Fact]
    public void both_attribute_forms_expose_the_contract_type()
    {
        new WolverineGrpcCodeFirstContractAttribute(typeof(IAssemblyRegisteredService))
            .ContractType.ShouldBe(typeof(IAssemblyRegisteredService));

        new WolverineGrpcCodeFirstContractAttribute<IAssemblyRegisteredService>()
            .ContractType.ShouldBe(typeof(IAssemblyRegisteredService));
    }

    [Fact]
    public void the_generic_form_is_found_through_the_base_attribute_type()
    {
        // The reader asks for the base type; the generic twin must come back with it.
        GrpcGraph.FindAssemblyRegisteredCodeFirstContracts([typeof(IAssemblyRegisteredService).Assembly])
            .ShouldBe([typeof(IAssemblyRegisteredService)]);
    }

    [Fact]
    public void the_attribute_scan_does_not_find_the_contract_but_the_assembly_reader_does()
    {
        typeof(IAssemblyRegisteredService).IsDefined(typeof(WolverineGrpcServiceAttribute), false).ShouldBeFalse();

        GrpcGraph.FindCodeFirstServiceContracts([typeof(IAssemblyRegisteredService).Assembly])
            .ShouldNotContain(typeof(IAssemblyRegisteredService));
    }

    [Fact]
    public void discovery_builds_a_code_first_chain_from_the_assembly_attribute_alone()
    {
        var graph = buildGraph(typeof(IAssemblyRegisteredService).Assembly);

        graph.DiscoverServices(new WolverineGrpcOptions());

        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(IAssemblyRegisteredService)).ShouldBe(1);
    }

    [Fact]
    public void attribute_and_options_registering_the_same_contract_yield_one_chain()
    {
        var graph = buildGraph(typeof(IAssemblyRegisteredService).Assembly);

        graph.DiscoverServices(new WolverineGrpcOptions().IncludeCodeFirstContract<IAssemblyRegisteredService>());

        graph.CodeFirstChains.Count(c => c.ServiceContractType == typeof(IAssemblyRegisteredService)).ShouldBe(1);
    }

    [Fact]
    public void an_attribute_in_an_assembly_wolverine_does_not_scan_is_not_read()
    {
        // Same rule as an attributed interface: the attribute has to sit in WolverineOptions.Assemblies.
        var graph = buildGraph(typeof(GreeterGrpcService).Assembly);

        graph.DiscoverServices(new WolverineGrpcOptions());

        graph.CodeFirstChains.ShouldNotContain(c => c.ServiceContractType == typeof(IAssemblyRegisteredService));
    }

    [Fact]
    public void an_attribute_naming_an_invalid_type_fails_discovery_with_the_assembly_and_type_named()
    {
        var source = typeof(assembly_attribute_registration_unit_tests).Assembly;

        var ex = Should.Throw<InvalidOperationException>(() =>
            GrpcGraph.ReadAssemblyRegisteredCodeFirstContracts(source,
                [new WolverineGrpcCodeFirstContractAttribute(typeof(NotAnInterfaceContract))]).ToList());

        ex.Message.ShouldContain("[assembly: WolverineGrpcCodeFirstContract]");
        ex.Message.ShouldContain(source.GetName().Name!);
        ex.Message.ShouldContain(nameof(NotAnInterfaceContract));
        ex.Message.ShouldContain("not an interface");
    }

    [Fact]
    public void an_attribute_naming_an_interface_without_service_contract_is_rejected()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            GrpcGraph.ReadAssemblyRegisteredCodeFirstContracts(typeof(INotAServiceContract).Assembly,
                [new WolverineGrpcCodeFirstContractAttribute<INotAServiceContract>()]).ToList());

        ex.Message.ShouldContain("[System.ServiceModel.ServiceContract]");
    }

    [Fact]
    public void the_attribute_rejects_a_null_type()
    {
        Should.Throw<ArgumentNullException>(() => new WolverineGrpcCodeFirstContractAttribute(null!));
    }

    private static GrpcGraph buildGraph(System.Reflection.Assembly applicationAssembly)
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
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

        return new GrpcGraph(options, container);
    }
}
