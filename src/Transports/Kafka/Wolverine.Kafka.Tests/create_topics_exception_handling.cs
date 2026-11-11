using Dekaf.Admin;
using Dekaf.Errors;
using Dekaf.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

// AutoProvision idempotency: a create against an existing topic throws a KafkaException carrying
// ErrorCode.TopicAlreadyExists, which SetupOnAsync must swallow — while every other failure
// (authorization, invalid config, ...) must propagate and fail startup loudly.
public class create_topics_exception_handling
{
    [Fact]
    public async Task single_topic_tolerates_topic_already_exists()
    {
        await SetupSingleTopic(KafkaException.FromErrorCode(ErrorCode.TopicAlreadyExists,
            "Topic 'topic' already exists."));
    }

    [Fact]
    public async Task single_topic_propagates_authorization_failure()
    {
        await ShouldPropagateFromSingleTopic(KafkaException.FromErrorCode(
            ErrorCode.TopicAuthorizationFailed, "denied"));
    }

    [Fact]
    public async Task single_topic_propagates_unrelated_error_with_misleading_text()
    {
        await ShouldPropagateFromSingleTopic(KafkaException.FromErrorCode(
            ErrorCode.TopicAuthorizationFailed, "already exists."));
    }

    [Fact]
    public async Task single_topic_propagates_a_kafka_exception_without_an_error_code()
    {
        await ShouldPropagateFromSingleTopic(new KafkaException("something went sideways"));
    }

    [Fact]
    public async Task topic_group_continues_after_topic_already_exists()
    {
        var visited = new List<string>();
        var group = new KafkaTopicGroup(
            new KafkaTransport(), ["one", "two"], EndpointRole.Application)
        {
            CreateTopicFunc = (_, topic) =>
            {
                visited.Add(topic);
                return topic == "one"
                    ? Task.FromException(KafkaException.FromErrorCode(ErrorCode.TopicAlreadyExists,
                        "The topic has already been created"))
                    : Task.CompletedTask;
            }
        };

        await group.SetupOnAsync(
            Substitute.For<IAdminClient>(), NullLogger.Instance);

        visited.ShouldBe(["one", "two"]);
    }

    [Fact]
    public async Task topic_group_propagates_other_errors()
    {
        var expected = KafkaException.FromErrorCode(ErrorCode.TopicAuthorizationFailed, "denied");
        var group = new KafkaTopicGroup(
            new KafkaTransport(), ["one", "two"], EndpointRole.Application)
        {
            CreateTopicFunc = (_, _) => Task.FromException(expected)
        };

        var thrown = await Should.ThrowAsync<KafkaException>(
            () => group.SetupOnAsync(
                Substitute.For<IAdminClient>(), NullLogger.Instance).AsTask());

        thrown.ShouldBeSameAs(expected);
    }

    private static Task SetupSingleTopic(KafkaException exception)
    {
        var topic = new KafkaTopic(new KafkaTransport(), "topic", EndpointRole.Application)
        {
            CreateTopicFunc = (_, _) => Task.FromException(exception)
        };

        return topic.SetupOnAsync(
            Substitute.For<IAdminClient>(), NullLogger.Instance).AsTask();
    }

    private static async Task ShouldPropagateFromSingleTopic(KafkaException expected)
    {
        var thrown = await Should.ThrowAsync<KafkaException>(
            () => SetupSingleTopic(expected));

        thrown.ShouldBeSameAs(expected);
    }
}
