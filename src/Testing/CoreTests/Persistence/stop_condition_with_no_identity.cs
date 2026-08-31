using JasperFx.CodeGeneration.Model;
using Shouldly;
using Wolverine.Middleware;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// <c>IChain.AddStopConditionIfNull(Variable, Variable?, IDataRequirement)</c> declares its identity
/// parameter nullable, but both implementations dereferenced it as <c>identity!</c> straight into the
/// guard frames. Passing the null the signature invites therefore crashed code generation instead of
/// producing a guard. These tests pin the seam.
/// </summary>
public class stop_condition_with_no_identity
{
    private static readonly Variable theEntity = new(typeof(Invoice), "invoice");

    private static HandlerChain aChain() =>
        HandlerChain.For<StopConditionHandler>(x => x.Handle(null!), null!);

    [Fact]
    public void the_stock_message_names_the_entity_type_rather_than_an_id_it_does_not_have()
    {
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute { OnMissing = OnMissing.ThrowException });

        var frame = frames.ShouldHaveSingleItem().ShouldBeOfType<ThrowRequiredDataMissingExceptionFrame>();

        frame.Identity.ShouldBeNull();

        // Deliberately not "Unknown Invoice with identity {Id}" -- there is no id to substitute, so
        // the message must not ask for one
        frame.Message.ShouldBe("Required Invoice was not found");
    }

    [Fact]
    public void a_supplied_missing_message_is_used_verbatim()
    {
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute
            {
                OnMissing = OnMissing.ThrowException,
                MissingMessage = "That invoice has not been written yet"
            });

        frames.ShouldHaveSingleItem().ShouldBeOfType<ThrowRequiredDataMissingExceptionFrame>()
            .Message.ShouldBe("That invoice has not been written yet");
    }

    [Fact]
    public void an_id_placeholder_in_a_supplied_message_is_left_alone_when_there_is_no_id()
    {
        // Nothing to substitute, and silently deleting the placeholder would hide the mistake. The
        // frame emits the message as it stands.
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute
            {
                OnMissing = OnMissing.ThrowException,
                MissingMessage = "No invoice {Id}"
            });

        frames.ShouldHaveSingleItem().ShouldBeOfType<ThrowRequiredDataMissingExceptionFrame>()
            .Message.ShouldBe("No invoice {Id}");
    }

    [Fact]
    public void an_identity_is_still_carried_through_when_there_is_one()
    {
        var identity = new Variable(typeof(string), "number");

        var frames = aChain().AddStopConditionIfNull(theEntity, identity,
            new EntityAttribute { OnMissing = OnMissing.ThrowException });

        var frame = frames.ShouldHaveSingleItem().ShouldBeOfType<ThrowRequiredDataMissingExceptionFrame>();

        frame.Identity.ShouldBeSameAs(identity);
        frame.Message.ShouldBe("Unknown Invoice with identity {Id}");
    }

    [Theory]
    [InlineData(OnMissing.Simple404)]
    [InlineData(OnMissing.ProblemDetailsWith400)]
    [InlineData(OnMissing.ProblemDetailsWith404)]
    [InlineData(OnMissing.EmptyContentWith204)]
    public void the_log_and_stop_conditions_never_needed_an_identity(OnMissing onMissing)
    {
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute { OnMissing = onMissing });

        frames.Length.ShouldBe(2);
        frames[1].ShouldBeOfType<HandlerContinuationFrame>();
    }
}

// Top level rather than nested, because NameInCode() renders a nested type as
// "stop_condition_with_no_identity.Invoice" and these assertions are about the message a user reads.
public record Invoice(string Number);

public record StopConditionMessage(string Number);

public class StopConditionHandler
{
    public void Handle(StopConditionMessage message)
    {
    }
}
