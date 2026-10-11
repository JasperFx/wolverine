using Bobcat.Generated.EventModel;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Tests;

// bobcat#471 step 2 / bobcat#462: the [EventModelRole] attributes on WolverineScenario's methods and their
// WolverineSpec twins reach the Event Model through Bobcat's generator, across the package boundary.
// projected_lane is an unbound [BobcatFeature], so its slice is named after the feature.
public class projected_event_model_roles
{
    [Fact]
    public void the_wolverine_vocabulary_stamps_the_command_and_the_message_of_a_projected_spec()
    {
        var slice = BobcatEventModelSource.Describe().Slices.Single(x => x.Name == "Confirming appointments");

        // WhenReceived(message): the act's command
        slice.CommandType.ShouldNotBeNull().Name.ShouldBe(nameof(ConfirmAppointment));
        slice.Pattern.ShouldBe(SlicePattern.Command);

        // ThenMessageSent<T>(): a published message
        slice.PublishedMessages.Select(x => x.Name).ShouldBe([nameof(SendConfirmationEmail)]);

        // A WhenReceived act declares no trigger kind
        slice.TriggerKind.ShouldBeNull();

        slice.Specifications.ShouldHaveSingleItem().Identity
            .ShouldBe("Confirming appointments/a scheduled appointment is confirmed");
    }
}
