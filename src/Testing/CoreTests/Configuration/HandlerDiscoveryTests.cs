using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Configuration;

public class HandlerDiscoveryTests
{
    public class OpenGuy<T>;
    public interface InterfaceGuy;
    public abstract class AbstractGuy;

    [Theory]
    [InlineData(typeof(InternalGuy))]
    [InlineData(typeof(OpenGuy<>))]
    [InlineData(typeof(InterfaceGuy))]
    [InlineData(typeof(AbstractGuy))]
    public void assert_on_invalid_handler_types(Type candidateType)
    {
        var source = new HandlerDiscovery();
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            source.IncludeType(candidateType);
        });
    }

    public record Message;
    public class MyCustomListener
    {
        public void Handle(Message message){}
    }

    [Fact]
    public void disabling_conventional_discovery_does_not_disable_custom_discovery()
    {
        var discovery = new HandlerDiscovery();
        discovery.DisableConventionalDiscovery();
        discovery.CustomizeHandlerDiscovery(q => q.Includes.WithNameSuffix("CustomListener"));

        var discoveredHandlers = discovery.FindCalls(new WolverineOptions())
            .Select(x => x.Item1)
            .ToList();
        
        discoveredHandlers.ShouldHaveSingleItem();
        discoveredHandlers.ShouldContain(typeof(MyCustomListener));
    }
    
    [Fact]
    public void disabling_conventional_discovery_does_not_exclude_explicit_types()
    {
        var discovery = new HandlerDiscovery();
        discovery.DisableConventionalDiscovery();
        discovery.IncludeType<MyCustomListener>();
        
        var discoveredHandlers = discovery.FindCalls(new WolverineOptions())
            .Select(x => x.Item1)
            .ToList();

        discoveredHandlers.ShouldHaveSingleItem();
        discoveredHandlers.ShouldContain(typeof(MyCustomListener));
    }
    
    [Fact]
    public void disabling_conventional_discovery_without_custom_discovery_should_not_find_any_handlers()
    {
        var discovery = new HandlerDiscovery();
        discovery.DisableConventionalDiscovery();
        
        var discoveredHandlers = discovery.FindCalls(new WolverineOptions())
            .Select(x => x.Item1)
            .ToList();

        discoveredHandlers.ShouldBeEmpty();
    }
}

internal static class InternalGuy;

