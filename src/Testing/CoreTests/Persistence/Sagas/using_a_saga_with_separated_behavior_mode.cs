using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Tracking;
using JasperFx.CodeGeneration;
using Xunit;

namespace CoreTests.Persistence.Sagas;

public class using_a_saga_with_separated_behavior_mode
{
    [Fact]
    public async Task able_to_use_separated_behaviors_with_sagas()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TrackedThing))
                    .IncludeType(typeof(OtherThingUpdatedHandler));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();

        TrackedThing.Updates = 0;

        await host.InvokeMessageAndWaitAsync(new StartTracking(id));

        var tracked = await host.SendMessageAndWaitAsync(new ThingUpdated(id));
        var envelopes = tracked.Executed.Envelopes().Where(x => x.Message is ThingUpdated).ToArray();
        envelopes.Length.ShouldBe(2);

        // GH-5136. The saga no longer keeps the message type's queue when it shares the message with another
        // handler type: each chain is on the queue named for its own handler type
        envelopes.Any(x => x.Destination == new Uri("local://coretests.persistence.sagas.trackedthing/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://coretests.persistence.sagas.otherthingupdatedhandler/")).ShouldBeTrue();

        TrackedThing.Updates.ShouldBe(1);
    }
}

public record StartTracking(Guid Id);

public record ThingUpdated(Guid Id);


public class TrackedThing : Saga
{
    public Guid Id { get; set; }
    public static int Updates { get; set; }

    public static TrackedThing Start(StartTracking cmd) => new TrackedThing { Id = cmd.Id };

    public void Handle(ThingUpdated updated, Envelope envelope)
    {
        Updates++;
        Debug.WriteLine(envelope.Destination);
    }
}

[WolverineIgnore]
public static class OtherThingUpdatedHandler
{
    public static void Handle(ThingUpdated updated)
    {
        Debug.WriteLine("Got updated for " + updated.Id);
    }
}
