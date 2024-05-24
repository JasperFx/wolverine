using Microsoft.Extensions.DependencyInjection;
using Wolverine.Codegen;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Codegen;

public class dealing_with_bi_directional_dependencies
{
    [Fact]
    public void can_slide_around_bi_directional_dependencies()
    {
        var services = new ServiceCollection();
        var graph = new ServiceContainer(services, services.BuildServiceProvider());
        
        graph.CouldResolve(typeof(Bar)).ShouldBeFalse();
        graph.CouldResolve(typeof(Baz)).ShouldBeFalse();
        graph.CouldResolve(typeof(Foo)).ShouldBeFalse();
        
    }
}

public class Bar
{
    public Bar(Baz baz)
    {
    }
}

public class Baz
{
    public Baz(Foo foo)
    {
    }
}

public class Foo
{
    public Foo(Bar bar)
    {
    }
}