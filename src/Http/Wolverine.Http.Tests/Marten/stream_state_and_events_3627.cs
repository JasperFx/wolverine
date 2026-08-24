using System.Text.Json;
using Alba;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Persistence.EventSourcing;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-3627. [StreamState] and [StreamEvents] for handlers whose read is the raw stream rather than the
/// folded aggregate -- timeline and audit shaped endpoints that [ReadAggregate] cannot express, because
/// folding has already collapsed what they need.
/// </summary>
public class stream_state_and_events_3627(AppFixture fixture) : IntegrationContext(fixture)
{
    private async Task<Guid> createOrderAsync()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Hat", "Shirt"])).ToUrl("/orders/create");
            x.StatusCodeShouldBeOk();
        });

        var status = await result.ReadAsJsonAsync<OrderStatus>();
        return status!.OrderId;
    }

    [Fact]
    public async Task both_reads_resolve_against_the_same_stream()
    {
        var id = await createOrderAsync();

        var result = await Host.Scenario(x =>
        {
            x.Get.Url($"/orders/{id}/timeline");
            x.StatusCodeShouldBeOk();
        });

        var timeline = (await result.ReadAsJsonAsync<OrderTimeline>()).ShouldNotBeNull();

        // StreamState carried the version, StreamEvents carried the events themselves
        timeline.Version.ShouldBe(1);

        // Marten's event ALIAS, not the CLR name -- which is itself the proof that this read went
        // through Marten's event store rather than anything the test could have fabricated
        timeline.EventTypes.ShouldContain("order_created");
    }

    [Fact]
    public async Task a_missing_stream_is_a_404_when_the_parameter_is_not_nullable()
    {
        // [StreamState] StreamState state -- non-nullable, so the standard not-found guard applies,
        // exactly as [ReadModel] behaves since GH-3929
        await Host.Scenario(x =>
        {
            x.Get.Url($"/orders/{Guid.NewGuid()}/timeline");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_nullable_parameter_leaves_absence_to_the_handler()
    {
        // [StreamState] StreamState? state -- the author said "I will handle absence", so no guard
        var result = await Host.Scenario(x =>
        {
            x.Get.Url($"/orders/{Guid.NewGuid()}/timeline-optional");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsJsonAsync<OrderTimeline>())!.Version.ShouldBe(0);
    }

    [Fact]
    public void stream_events_deliberately_has_no_required_knob()
    {
        // A missing stream yields an EMPTY LIST, not null, so the null-guard model the rest of the
        // IDataRequirement family is built on has nothing to test. Pinned so nobody "fixes" the
        // asymmetry by adding one -- pair with [StreamState] for existence guards instead
        typeof(StreamEventsAttribute).GetProperty("Required").ShouldBeNull();
        typeof(StreamEventsAttribute).IsAssignableTo(typeof(Wolverine.Persistence.IDataRequirement))
            .ShouldBeFalse();

        // ...while [StreamState] does have one
        typeof(StreamStateAttribute).GetProperty("Required").ShouldNotBeNull();
    }
}
