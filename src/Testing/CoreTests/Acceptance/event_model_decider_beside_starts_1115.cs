using JasperFx.Events.EventModeling;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel1115;

// jasperfx#1115 / jasperfx#1119: a slice that declares DeciderModel<T>() beside StartsStream<T>() keeps
// both roles, so the report prints a decider: line beside the starts: line
public class event_model_decider_beside_starts_1115
{
    [Fact]
    public void describe_event_model_prints_the_decider_beside_starts()
    {
        var builder = new EventModelBuilder();
        builder.Command("OpenAccount").StartsStream<Account>().DeciderModel<OpenAccountDecider>()
            .Emits("AccountOpened");
        var model = builder.Build("app").WithProvenance(EventModelProvenance.Declared);

        var lines = EventModelReport.Plain(model).ReplaceLineEndings("\n").Split('\n').Select(x => x.Trim()).ToList();

        lines.ShouldContain($"starts: the {nameof(Account)} stream");
        lines.ShouldContain($"decider: {nameof(OpenAccountDecider)} (DCB)");
    }

    [Fact]
    public void a_slice_without_a_decider_prints_no_decider_line()
    {
        var builder = new EventModelBuilder();
        builder.Command("OpenAccount").StartsStream<Account>().Emits("AccountOpened");

        var text = EventModelReport.Plain(builder.Build("app").WithProvenance(EventModelProvenance.Declared));

        text.ShouldNotContain("decider:");
    }

    public class Account;

    public class OpenAccountDecider;
}
