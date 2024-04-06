using JasperFx.Core.Reflection;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class dead_letter_endpoints(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public async Task fetch_them()
    {
        // Given
        var exceptionMessage = Guid.NewGuid().ToString();
        await Scenario(x =>
        {
            x.Post.Json(new MessageThatAlwaysGoesToDeadLetter(exceptionMessage)).ToUrl("/send/always-dead-letter");
            x.StatusCodeShouldBe(202);
        });

        // When
        var result = await Scenario(x =>
        {
            x.Post.Json(new DeadLetterEnvelopeGetRequest
            {
                ExceptionMessage = exceptionMessage
            }).ToUrl("/dead-letters");
        });

        // Expect
        var deadletters = result.ReadAsJson<DeadLetterEnvelopesFoundResponse>();
        deadletters
            .ShouldNotBeNull().Messages.Count.ShouldBe(1);
        deadletters.Messages.First().ExceptionType.ShouldBe(typeof(AlwaysDeadLetterException).FullNameInCode());
        deadletters.Messages.First().ExceptionMessage.ShouldBe(exceptionMessage);
        deadletters.Messages.First().Body.ShouldNotBeNull();
        deadletters.Messages.First().Id.ShouldNotBe(default);
        deadletters.Messages.First().MessageType.ShouldNotBeNull();
        deadletters.Messages.First().ReceivedAt.ShouldNotBeNull();
        deadletters.Messages.First().Source.ShouldNotBeNull();
        deadletters.Messages.First().SentAt.ShouldNotBe(default);
        deadletters.Messages.First().Replayable.ShouldBeFalse();
    }
}
