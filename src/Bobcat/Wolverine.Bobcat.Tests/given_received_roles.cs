using Bobcat;
using JasperFx.Events.EventModeling;

// GH-5076: GivenReceived carries a Document role, so the arranged message is run evidence -- in the specification's
// resolved types -- and never the slice's act. The spec below is compiled for the generator and never run.

namespace Wolverine.Bobcat.Tests.GivenReceivedRoles
{
    [BobcatFeature("Arranged messages")]
    public class arranged_message_specs : WolverineSpec
    {
        public arranged_message_specs() : base((IServiceProvider)null!) { }

        [Fact(Explicit = true), BobcatSlice(SliceName = "ConfirmAfterArranging")]
        public async Task an_appointment_scheduled_by_its_handler_is_confirmed()
        {
            var id = Guid.NewGuid();
            await GivenReceived(new ScheduleAppointment(id, new Patient("Ann", new Address("Austin")), []));
            await WhenReceived(new ConfirmAppointment(id));
        }
    }

    public class GivenReceivedRoleTests
    {
        [Fact]
        public void an_arranged_message_is_resolved_but_the_act_is_the_when()
        {
            var slice = GeneratedEventModel.For(typeof(GivenReceivedRoleTests).Assembly)!.Slices
                .Single(x => x.Name == "ConfirmAfterArranging");

            slice.CommandType.ShouldNotBeNull().Name.ShouldBe(nameof(ConfirmAppointment));
            slice.Specifications.ShouldHaveSingleItem().ResolvedTypes.Select(x => x.Name)
                .ShouldContain(nameof(ScheduleAppointment));
        }
    }
}
