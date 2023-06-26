using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.DependencyInjection;
using Module2;
using OrderExtension;
using TestingSupport;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Configuration;

public class find_handlers_with_the_default_handler_discovery : IntegrationContext
{
    private readonly ITestOutputHelper _output;

    public find_handlers_with_the_default_handler_discovery(DefaultApp @default, ITestOutputHelper output) : base(@default)
    {
        _output = output;
        @default.RecycleIfNecessary();
    }

    [Fact]
    public void can_find_appropriate_static_method()
    {
        chainFor<MovieRemoved>().Handlers.Any().ShouldBeTrue();
    }

    [Fact]
    public void can_find_handlers_from_static_classes()
    {
        chainFor<StaticClassMessage>().Handlers.Single().HandlerType
            .ShouldBe(typeof(StaticClassHandler));
    }

    [Fact]
    public void does_not_find_handlers_that_do_not_match_the_type_naming_convention()
    {
        chainFor<MovieAdded>().ShouldNotHaveHandler<MovieWatcher>(x => x.Handle(null));
    }

    [Fact]
    public void finds_classes_suffixed_as_Consumer()
    {
        chainFor<Event1>().ShouldHaveHandler<EventConsumer>(x => x.Consume(new Event1()));
    }

    [Fact]
    public void finds_classes_suffixed_as_Handler()
    {
        chainFor<MovieAdded>().ShouldHaveHandler<NetflixHandler>(x => x.Consume(new MovieAdded()));
    }


    [Fact]
    public void ignore_class_marked_as_NotHandler()
    {
        chainFor<MovieAdded>()
            .ShouldNotHaveHandler<BlockbusterHandler>(x => x.Handle(new MovieAdded()));
    }

    [Fact]
    public void ignore_method_marked_as_NotHandler()
    {
        with(x => x.DisableConventionalDiscovery().IncludeType<NetflixHandler>());
        //withAllDefaults();
        chainFor<MovieAdded>()
            .ShouldNotHaveHandler<NetflixHandler>(x => x.Handles(new MovieAdded()));
    }

    [Fact]
    public void will_find_methods_with_parameters_other_than_the_message()
    {
        chainFor<MovieAdded>().ShouldHaveHandler<NetflixHandler>(x => x.Handle(null, null));
    }

    [Fact]
    public void find_handlers_from_wolverine_module_extensions()
    {
        _output.WriteLine(Host.Services.GetRequiredService<IWolverineRuntime>().Options.DescribeHandlerMatch(typeof(OrderHandler)));
        
        chainFor<CreateOrder>().ShouldHaveHandler<OrderHandler>(x => x.HandleAsync(new CreateOrder()));
        chainFor<ShipOrder>().ShouldHaveHandler<OrderHandler>(x => x.HandleAsync(new ShipOrder()));
    }
    
    [WolverineHandler]
    public static class AttributeWorker
    {
        public static void Handle(AttributeMessage message)
        {
            Console.WriteLine("Got it");
        }
    }

    public record AttributeMessage;

    [Fact]
    public void find_handlers_marked_with_wolverine_handler_attribute()
    {
        chainFor<AttributeMessage>().ShouldNotBeNull();
    }

    public record MarkedMessage;
    public class MarkedWorker : IWolverineHandler
    {
        public void Handle(MarkedMessage message)
        {
            Console.WriteLine("Got it.");
        }
    }

    [Fact]
    public void finds_handlers_that_implement_IWolverineHandler()
    {
        chainFor<MarkedMessage>().ShouldHaveHandler<MarkedWorker>(x => x.Handle(null));
    }
}


public class customized_finding : IntegrationContext
{
    public customized_finding(DefaultApp @default) : base(@default)
    {
    }

    private void withTypeDiscovery(Action<TypeQuery> customize)
    {
        with(opts =>
        {
            opts.Discovery.CustomizeHandlerDiscovery(customize);
        });
    }

    [Fact]
    public void extra_suffix()
    {
        withTypeDiscovery(x => x.Includes.WithNameSuffix("Watcher"));

        chainFor<MovieAdded>().ShouldHaveHandler<MovieWatcher>(x => x.Handle(null));
    }

    [Fact]
    public void handler_types_from_a_marker_interface()
    {
        withTypeDiscovery(x => x.Includes.Implements<IMovieThing>());

        chainFor<MovieAdded>().ShouldHaveHandler<EpisodeWatcher>(x => x.Handle(new MovieAdded()));
    }

    public record DifferentNameMessage;

    public class DifferentNameMessageHandler
    {
        [WolverineHandler] // This should force Wolverine into thinking
                           // it's a handler
        public void DoWork(DifferentNameMessage message)
        {
            
        }
    }

    [Fact]
    public void use_WolverineHandler_attribute_on_method()
    {
        chainFor<DifferentNameMessage>().ShouldHaveHandler<DifferentNameMessageHandler>(x => x.DoWork(null));
    }

    [Fact]
    public void find_handlers_from_included_assembly()
    {
        with(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(Module2Message1).Assembly);
        });

        chainFor<Module2Message1>().ShouldNotBeNull();
        chainFor<Module2Message2>().ShouldNotBeNull();
        chainFor<Module2Message3>().ShouldNotBeNull();
        chainFor<Module2Message4>().ShouldNotBeNull();
    }
}

public interface IMovieSink
{
    void Listen(MovieAdded added);
}

public interface IMovieThing
{
}

public class EpisodeWatcher : IMovieThing
{
    public void Handle(MovieAdded added)
    {
    }
}

public abstract class MovieEvent : IMovieEvent
{
}

public class MovieAdded : MovieEvent
{
}

public class MovieRemoved : MovieEvent
{
}

public class EpisodeAvailable
{
}

public class NewShow
{
}

public interface IMovieEvent
{
}

public class MovieWatcher
{
    public void Handle(MovieAdded added)
    {
    }
}

public class StaticClassMessage
{
}

public static class StaticClassHandler
{
    public static void Handle(StaticClassMessage message)
    {
    }
}

#region sample_WolverineIgnoreAttribute

public class NetflixHandler : IMovieSink
{
    public void Listen(MovieAdded added)
    {
    }

    public void Handles(IMovieEvent @event)
    {
    }

    public void Handles(MovieEvent @event)
    {
    }

    public void Consume(MovieAdded added)
    {
    }

    // Only this method will be ignored as
    // a handler method
    [WolverineIgnore]
    public void Handles(MovieAdded added)
    {
    }

    public void Handle(MovieAdded message, IMessageContext context)
    {
    }

    public static Task Handle(MovieRemoved removed)
    {
        return Task.CompletedTask;
    }
}

// All methods on this class will be ignored
// as handler methods even though the class
// name matches the discovery naming conventions
[WolverineIgnore]
public class BlockbusterHandler
{
    public void Handle(MovieAdded added)
    {
    }
}

#endregion

public class Event1
{
}

public class Event2
{
}

public class Event3
{
}

public class Event4
{
}

public class EventConsumer
{
    public void Consume(Event1 @event)
    {
    }
}

