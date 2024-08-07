using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class using_container_or_service_provider_in_handlers : CompilationContext
{
    private readonly ITestOutputHelper _output;

    public using_container_or_service_provider_in_handlers(ITestOutputHelper output)
    {
        _output = output;

        IfWolverineIsConfiguredAs(opts =>
        {
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            opts.IncludeType<CSP3Handler>();
            opts.IncludeType<CSP4Handler>();
        });
    }

    [Fact]
    public async Task IServiceProvider_as_constructor_dependency()
    {
        await Execute(new CSP3());
    }

    [Fact]
    public async Task IServiceProvider_as_method_parameter()
    {
        await Execute(new CSP4());
    }
}

public class CSP3;

public class CSP3Handler
{
    private readonly IServiceProvider _container;

    public CSP3Handler(IServiceProvider container)
    {
        _container = container;
    }

    public void Handle(CSP3 message)
    {
        _container.ShouldNotBeNull();
    }
}

public class CSP4;

public class CSP4Handler
{
    public void Handle(CSP4 message, IServiceProvider container)
    {
        container.ShouldNotBeNull();
    }
}