using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using JasperFx.Descriptors;
using Wolverine.Tracking;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-4000. The HTTP half of the per-route slice, and the sibling of
/// <c>Wolverine.Grpc.Tests.grpc_event_model_slice_4000</c>. A consumer walking endpoint by endpoint now
/// sees the slice next to the route rather than only through the assembled model.
/// </summary>
/// <remarks>
/// This could not be declared before JasperFx 2.55.0. <c>HttpChainDescriptor</c> lives in JasperFx and
/// <c>EventModelSliceDescriptor</c> lived in JasperFx.Events, which references it -- so the descriptor
/// could not name the type it needed and the slot was undeclarable. jasperfx#694 moved the wire
/// descriptors down, which is what unblocked this.
/// </remarks>
public class http_event_model_slice_4000(AppFixture fixture) : IntegrationContext(fixture)
{
    private async Task<HttpChainDescriptor> descriptorFor(string method, string route)
    {
        var capabilities = await ServiceCapabilities.ReadFrom(Host.GetRuntime(), null, CancellationToken.None);

        return capabilities.HttpGraphs
            .SelectMany(x => x.Chains)
            .Single(x => x.Route == route && x.HttpMethods.Contains(method));
    }

    [Fact]
    public async Task a_write_route_carries_the_slice_it_is()
    {
        // POST /orders/ship3 is Ship3(ShipOrder, [WriteAggregate] Order) -- the same chain
        // event_model_roles_3988 uses, so the two tests describe one route from both directions
        var descriptor = await descriptorFor("POST", "/orders/ship3");

        var slice = descriptor.EventModel.ShouldNotBeNull();

        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin.ShouldNotBeNull();
        slice.TriggerOrigin.HttpRoute.ShouldBe("/orders/ship3");
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");

        slice.AggregateTypes.Select(x => x.Name).ShouldBe([nameof(Order)]);
        slice.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));
    }

    [Fact]
    public async Task every_route_that_derives_a_slice_agrees_with_the_assembled_model()
    {
        // The property the whole design rests on: the per-route slice and the assembled model are
        // derived by the SAME method, so they cannot drift. Asserted rather than assumed
        var capabilities = await ServiceCapabilities.ReadFrom(Host.GetRuntime(), null, CancellationToken.None);

        capabilities.EventModel.ShouldNotBeNull();
        var assembled = capabilities.EventModel!.Slices.ToDictionary(x => x.Name);

        var carried = capabilities.HttpGraphs
            .SelectMany(x => x.Chains)
            .Where(x => x.EventModel is not null)
            .ToArray();

        carried.ShouldNotBeEmpty();

        foreach (var descriptor in carried)
        {
            var slice = descriptor.EventModel!;
            slice.TriggerKind.ShouldBe(TriggerKind.Http);

            if (assembled.TryGetValue(slice.Name, out var fromModel))
            {
                // Same name, same roles. The assembled model folds slices by name and keeps the FIRST
                // trigger, so trigger fields are deliberately not compared here
                slice.CommandType?.FullName.ShouldBe(fromModel.CommandType?.FullName);
                slice.AggregateTypes.Select(x => x.FullName).OrderBy(x => x)
                    .ShouldBe(fromModel.AggregateTypes.Select(x => x.FullName).OrderBy(x => x));
            }
        }
    }
}
