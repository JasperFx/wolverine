using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class smoke_test_code_generation_of_endpoints_with_no_service_dependencies : IntegrationContext
{
    public smoke_test_code_generation_of_endpoints_with_no_service_dependencies(AppFixture fixture) : base(fixture)
    {
    }

    public static IEnumerable<object[]> Methods => typeof(FakeEndpoint).GetMethods()
        .Where(x => x.DeclaringType == typeof(FakeEndpoint)).Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Methods))]
    public void compile_without_error(MethodInfo method)
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<WolverineHttpOptions>();
        registry.AddTransient<IServiceVariableSource>(c => new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        var parent = new HttpGraph(new WolverineOptions { ApplicationAssembly = GetType().Assembly }, container);

        var endpoint = new HttpChain(new MethodCall(typeof(FakeEndpoint), method), parent);


        endpoint.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container.Services);
    }
}