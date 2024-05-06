using System;
using System.Threading.Tasks;
using Lamar;
using TestingSupport;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class using_container_or_service_provider_in_handlers : CompilationContext
{
    private readonly ITestOutputHelper _output;

    public using_container_or_service_provider_in_handlers(ITestOutputHelper output)
    {
        _output = output;

        theOptions.IncludeType<CSP1Handler>();
        theOptions.IncludeType<CSP2Handler>();
        theOptions.IncludeType<CSP3Handler>();
        theOptions.IncludeType<CSP4Handler>();
    }

    [Fact]
    public async Task icontainer_as_constructor_dependency()
    {
        var handler = HandlerFor<CSP1>();
        _output.WriteLine(handler.Chain.SourceCode);

        await Execute(new CSP1());
    }

    [Fact]
    public async Task icontainer_as_method_parameter()
    {
        var handler = HandlerFor<CSP2>();
        _output.WriteLine(handler.Chain.SourceCode);

        await Execute(new CSP2());
    }

    [Fact]
    public async Task IServiceProvider_as_constructor_dependency()
    {
        var handler = HandlerFor<CSP3>();
        _output.WriteLine(handler.Chain.SourceCode);

        await Execute(new CSP3());
    }

    [Fact]
    public async Task IServiceProvider_as_method_parameter()
    {
        var handler = HandlerFor<CSP4>();
        _output.WriteLine(handler.Chain.SourceCode);

        await Execute(new CSP4());
    }
}

public class CSP1;

public class CSP1Handler
{
    private readonly IContainer _container;

    public CSP1Handler(IContainer container)
    {
        _container = container;
    }

    public void Handle(CSP1 message)
    {
        (_container is INestedContainer).ShouldBeTrue();
        _container.ShouldNotBeNull();
    }
}

public class CSP2;

public class CSP2Handler
{
    public void Handle(CSP2 message, IContainer container)
    {
        (container is INestedContainer).ShouldBeTrue();
        container.ShouldNotBeNull();
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
        (_container is INestedContainer).ShouldBeTrue();
        _container.ShouldNotBeNull();
    }
}

public class CSP4;

public class CSP4Handler
{
    public void Handle(CSP4 message, IServiceProvider container)
    {
        (container is INestedContainer).ShouldBeTrue();
        container.ShouldNotBeNull();
    }
}