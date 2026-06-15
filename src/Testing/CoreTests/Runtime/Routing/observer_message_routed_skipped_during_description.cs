using Microsoft.Extensions.Hosting;
using NSubstitute;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Routing;

// GH-3088: WolverineSystemPart.FindResources() walks every discovered message type
// through RoutingFor as part of resource-setup-on-startup. While WithinDescription
// is true, MessageRoute is allowed to take a null Sender and a null Serializer
// (the endpoint may not have a DefaultSerializer assigned until transports finish
// initializing). The pre-fix code still fired Observer.MessageRouted on those
// degraded routes — and any observer that called MessageRoute.Describe()
// (e.g. CritterWatch) NRE'd on Serializer.ContentType, killing the host at
// AddResourceSetupOnStartup time.
//
// The fix gates Observer.MessageRouted on !WolverineSystemPart.WithinDescription.
// Observers re-fire from the real (post-startup) RoutingFor calls once the runtime
// is live, so suppressing during description loses no signal.
public class observer_message_routed_skipped_during_description
{
    [Fact]
    public async Task does_not_fire_during_description_but_fires_normally_outside()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug3088RoutingHandler>();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var fakeObserver = Substitute.For<IWolverineObserver>();
        runtime.Observer = fakeObserver;

        // Description mode: observer MUST NOT be invoked for the messageType,
        // because the MessageRoute under construction here may have a null
        // Serializer/Sender, and any observer calling MessageRoute.Describe()
        // would NRE on Serializer.ContentType.
        runtime.ClearRoutingFor(typeof(Bug3088RoutedMessage));
        WolverineSystemPart.WithinDescription = true;
        try
        {
            runtime.RoutingFor(typeof(Bug3088RoutedMessage));
        }
        finally
        {
            WolverineSystemPart.WithinDescription = false;
        }

        fakeObserver.DidNotReceive().MessageRouted(
            Arg.Is(typeof(Bug3088RoutedMessage)),
            Arg.Any<IMessageRouter>());

        // Normal mode: same call must fire the observer exactly once. The real
        // RoutingFor calls that drive CritterWatch's MessagingSubscription
        // batches happen here, not during FindResources(), so no signal is lost.
        runtime.ClearRoutingFor(typeof(Bug3088RoutedMessage));
        runtime.RoutingFor(typeof(Bug3088RoutedMessage));

        fakeObserver.Received(1).MessageRouted(
            Arg.Is(typeof(Bug3088RoutedMessage)),
            Arg.Any<IMessageRouter>());
    }
}

public record Bug3088RoutedMessage(string Text);

public class Bug3088RoutingHandler
{
    public void Handle(Bug3088RoutedMessage message) => _ = message;
}
