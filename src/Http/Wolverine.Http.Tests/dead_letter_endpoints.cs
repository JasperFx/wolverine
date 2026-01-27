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
        var deadletters = (result.ReadAsJson<IReadOnlyList<DeadLetterEnvelopeResults>>()).Single();
        
        
        deadletters
            .ShouldNotBeNull().Envelopes.Count.ShouldBe(1);
        deadletters.Envelopes[0].ExceptionType.ShouldBe(typeof(AlwaysDeadLetterException).FullNameInCode());
        deadletters.Envelopes[0].ExceptionMessage.ShouldBe(exceptionMessage);
        deadletters.Envelopes[0].Message.ShouldNotBeNull();
        deadletters.Envelopes[0].Id.ShouldNotBe(default);
        deadletters.Envelopes[0].MessageType.ShouldNotBeNull();
        deadletters.Envelopes[0].ReceivedAt.ShouldNotBeNull();
        deadletters.Envelopes[0].Source.ShouldNotBeNull();
        deadletters.Envelopes[0].SentAt.ShouldNotBe(default);
        deadletters.Envelopes[0].Replayable.ShouldBeFalse();
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
        var all = result.ReadAsJson<IReadOnlyList<DeadLetterEnvelopeResults>>();
        var id = all[0].Envelopes.Single().Id;
        
        await Scenario(x =>
        {
            x.Post.Json(new DeadLetterEnvelopeIdsRequest
            {
                Ids = [id]
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
        var deadletters = result.ReadAsJson<IReadOnlyList<DeadLetterEnvelopeResults>>();
        var id = deadletters[0].Envelopes[0].Id;
        
        await Scenario(x =>
        {
            x.Delete.Json(new DeadLetterEnvelopeIdsRequest
            {
                Ids = [id]
            }).ToUrl("/dead-letters");
        });
    }
}
