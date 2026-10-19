using Bobcat.Engine;
using JasperFx.Events.Tags;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// JasperFx/wolverine#4865: a tag query reads by the names its tags were given. Moved here from Bobcat's
// TagsAndStrongTypedIdsTests with EventStores.Describe (GH-5082, JasperFx/bobcat#755).
public class EventTagQueryDescriptionTests
{
    private readonly record struct ScreeningId(Guid Value);

    private readonly record struct SeatCode(string Value);

    private record SeatReserved(ScreeningId Screening, SeatCode Seat);

    private record SeatReleased(SeatCode Seat);

    [Fact]
    public void a_query_reads_as_the_tags_it_selects_by_with_any_event_types_it_narrows_to()
    {
        using var _ = ScenarioRecorder.Begin("Values", "a scenario", publisher: null, runId: Guid.NewGuid());
        var screening = ScenarioValues.Declare(new ScreeningId(Guid.NewGuid()), "theScreeningId");
        var seat = new SeatCode("4C");

        var query = EventTagQuery.For(screening).AndEventsOfType<SeatReserved, SeatReleased>().Or(seat);

        EventStores.Describe(query)
            .ShouldBe("events tagged #theScreeningId (SeatReserved, SeatReleased) or SeatCode(4C)");
    }
}
