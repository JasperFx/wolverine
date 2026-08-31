using JasperFx.CodeGeneration.Model;
using Shouldly;
using Wolverine.Http.Policies;
using Wolverine.Persistence;
using Xunit;

namespace Wolverine.Http.Tests.Persistence;

/// <summary>
/// The HTTP half of <c>CoreTests.Persistence.stop_condition_with_no_identity</c>.
/// <see cref="HttpChain.AddStopConditionIfNull(Variable, Variable?, IDataRequirement)" /> declares the
/// identity nullable and then dereferenced it as <c>identity!</c> into
/// <see cref="WriteProblemDetailsIfNull" />, so passing null crashed code generation.
/// </summary>
public class stop_condition_with_no_identity
{
    private static readonly Variable theEntity = new(typeof(Invoice), "invoice");

    // Any endpoint will do -- these tests are about the frames AddStopConditionIfNull hands back, not
    // about the endpoint. It has to be one with no attributed parameters, though: ChainFor builds its
    // chain with no parent HttpGraph, so parameter matching has no WolverineOptions to resolve.
    private static HttpChain aChain() =>
        HttpChain.ChainFor<StopConditionEndpoint>(x => x.Get());

    [Theory]
    [InlineData(OnMissing.ProblemDetailsWith400, 400)]
    [InlineData(OnMissing.ProblemDetailsWith404, 404)]
    public void problem_details_without_an_identity(OnMissing onMissing, int statusCode)
    {
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute { OnMissing = onMissing });

        var frame = frames.ShouldHaveSingleItem().ShouldBeOfType<WriteProblemDetailsIfNull>();

        frame.Identity.ShouldBeNull();
        frame.StatusCode.ShouldBe(statusCode);

        // WriteProblems already accepted an object? identity, so "null" is a legal argument -- but the
        // stock message must not ask for an id that will never arrive
        frame.Message.ShouldBe("Required Invoice was not found");
    }

    [Fact]
    public void a_supplied_missing_message_is_used_verbatim()
    {
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute
            {
                OnMissing = OnMissing.ProblemDetailsWith404,
                MissingMessage = "That invoice has not been written yet"
            });

        frames.ShouldHaveSingleItem().ShouldBeOfType<WriteProblemDetailsIfNull>()
            .Message.ShouldBe("That invoice has not been written yet");
    }

    [Fact]
    public void throw_exception_without_an_identity()
    {
        var frames = aChain().AddStopConditionIfNull(theEntity, null,
            new EntityAttribute { OnMissing = OnMissing.ThrowException });

        var frame = frames.ShouldHaveSingleItem().ShouldBeOfType<ThrowRequiredDataMissingExceptionFrame>();

        frame.Identity.ShouldBeNull();
        frame.Message.ShouldBe("Required Invoice was not found");
    }

    [Fact]
    public void an_identity_is_still_carried_through_when_there_is_one()
    {
        var identity = new Variable(typeof(string), "number");

        var frames = aChain().AddStopConditionIfNull(theEntity, identity,
            new EntityAttribute { OnMissing = OnMissing.ProblemDetailsWith400 });

        frames.ShouldHaveSingleItem().ShouldBeOfType<WriteProblemDetailsIfNull>()
            .Identity.ShouldBeSameAs(identity);
    }
}

// Top level rather than nested, because NameInCode() renders a nested type as
// "stop_condition_with_no_identity.Invoice" and these assertions are about the message a user reads.
public record Invoice(string Number);

internal class StopConditionEndpoint
{
    [WolverineGet("/stop-condition")]
    public string Get() => "ok";
}
