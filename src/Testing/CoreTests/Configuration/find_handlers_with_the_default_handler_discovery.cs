using System.Linq;
using System.Threading.Tasks;
using OrderExtension;
using TestingSupport;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Configuration;

public class find_handlers_with_the_default_handler_discovery : IntegrationContext
{
    public find_handlers_with_the_default_handler_discovery(DefaultApp @default) : base(@default)
    {
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
        with(x => x.Handlers.DisableConventionalDiscovery().IncludeType<NetflixHandler>());
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
        chainFor<CreateOrder>().ShouldHaveHandler<OrderHandler>(x => x.Handle(new CreateOrder()));
        chainFor<ShipOrder>().ShouldHaveHandler<OrderHandler>(x => x.Handle(new ShipOrder()));
    }
}

public class customized_finding : IntegrationContext
{
    public customized_finding(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void extra_suffix()
    {
        with(x => x.Handlers.Discovery(d => d.IncludeClassesSuffixedWith("Watcher")));

        chainFor<MovieAdded>().ShouldHaveHandler<MovieWatcher>(x => x.Handle(null));
    }

    [Fact]
    public void handler_types_from_a_marker_interface()
    {
        with(x => x.Handlers.Discovery(d => d.IncludeTypesImplementing<IMovieThing>()));

        chainFor<MovieAdded>().ShouldHaveHandler<EpisodeWatcher>(x => x.Handle(new MovieAdded()));
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