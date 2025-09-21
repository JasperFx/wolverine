using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Tracking;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class dead_letter_endpoints(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public async Task fetch_them()
    {
        // Given
        var exceptionMessage = $"fetchable-{Guid.NewGuid()}";

        await Host.TrackActivity().DoNotAssertOnExceptionsDetected().ExecuteAndWaitAsync(_ =>
        {
            return Scenario(x =>
            {
                x.Post.Json(new MessageThatAlwaysGoesToDeadLetter(exceptionMessage)).ToUrl("/send/always-dead-letter");
                x.StatusCodeShouldBe(202);
            });
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
        deadletters.Messages[0].ExceptionType.ShouldBe(typeof(AlwaysDeadLetterException).FullNameInCode());
        deadletters.Messages[0].ExceptionMessage.ShouldBe(exceptionMessage);
        deadletters.Messages[0].Body.ShouldNotBeNull();
        deadletters.Messages[0].Id.ShouldNotBe(default);
        deadletters.Messages[0].MessageType.ShouldNotBeNull();
        deadletters.Messages[0].ReceivedAt.ShouldNotBeNull();
        deadletters.Messages[0].Source.ShouldNotBeNull();
        deadletters.Messages[0].SentAt.ShouldNotBe(default);
        deadletters.Messages[0].Replayable.ShouldBeFalse();
    }

    [Fact]
    public async Task replay_one()
    {
        // Given
        var exceptionMessage = $"replayable-{Guid.NewGuid()}";

        await Host.TrackActivity().DoNotAssertOnExceptionsDetected().ExecuteAndWaitAsync(_ =>
        {
            return Scenario(x =>
            {
                x.Post.Json(new MessageThatAlwaysGoesToDeadLetter(exceptionMessage)).ToUrl("/send/always-dead-letter");
                x.StatusCodeShouldBe(202);
            });
        });
        
        var result = await Scenario(x =>
        {
            x.Post.Json(new DeadLetterEnvelopeGetRequest
            {
                ExceptionMessage = exceptionMessage
            }).ToUrl("/dead-letters");
        });

        // When & Expect
        var deadletters = result.ReadAsJson<DeadLetterEnvelopesFoundResponse>();
        await Scenario(x =>
        {
            x.Post.Json(new DeadLetterEnvelopeIdsRequest
            {
                Ids = [deadletters.Messages.Single().Id]
            }).ToUrl("/dead-letters/replay");
        });
    }

    [Fact]
    public async Task delete_one()
    {
        // Given
        var exceptionMessage = $"deletable-{Guid.NewGuid()}";

        await Host.TrackActivity().DoNotAssertOnExceptionsDetected().ExecuteAndWaitAsync(_ =>
        {
            return Scenario(x =>
            {
                x.Post.Json(new MessageThatAlwaysGoesToDeadLetter(exceptionMessage)).ToUrl("/send/always-dead-letter");
                x.StatusCodeShouldBe(202);
            });
        });
        
        var result = await Scenario(x =>
        {
            x.Post.Json(new DeadLetterEnvelopeGetRequest
            {
                ExceptionMessage = exceptionMessage
            }).ToUrl("/dead-letters");
        });

        // When & Expect
        var deadletters = result.ReadAsJson<DeadLetterEnvelopesFoundResponse>();
        
        await Scenario(x =>
        {
            x.Delete.Json(new DeadLetterEnvelopeIdsRequest
            {
                Ids = [deadletters.Messages.Single().Id]
            }).ToUrl("/dead-letters");
        });
    }
}
