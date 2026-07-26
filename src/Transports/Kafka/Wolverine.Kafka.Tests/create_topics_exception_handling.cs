using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

public class create_topics_exception_handling
{
    [Fact]
    public async Task single_topic_tolerates_redpanda_topic_already_exists_reason()
    {
        await SetupSingleTopic(ExistingTopic("The topic has already been created"));
    }

    [Fact]
    public async Task single_topic_tolerates_multiple_topic_already_exists_results()
    {
        await SetupSingleTopic(ExceptionFor(
            ("one", ErrorCode.TopicAlreadyExists, "first"),
            ("two", ErrorCode.TopicAlreadyExists, "second")));
    }

    [Fact]
    public async Task single_topic_propagates_empty_results()
    {
        await ShouldPropagateFromSingleTopic(new CreateTopicsException([]));
    }

    [Fact]
    public async Task single_topic_propagates_mixed_results()
    {
        await ShouldPropagateFromSingleTopic(ExceptionFor(
            ("one", ErrorCode.TopicAlreadyExists, "exists"),
            ("two", ErrorCode.TopicAuthorizationFailed, "denied")));
    }

    [Fact]
    public async Task single_topic_propagates_unrelated_error()
    {
        await ShouldPropagateFromSingleTopic(ExceptionFor(
            ("topic", ErrorCode.TopicException, "invalid")));
    }

    [Fact]
    public async Task single_topic_propagates_unrelated_error_with_misleading_text()
    {
        var expected = ExceptionFor(
            ("topic", ErrorCode.TopicAuthorizationFailed, "already exists."));

        await ShouldPropagateFromSingleTopic(expected);
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
                    ? Task.FromException(ExistingTopic(
                        "The topic has already been created"))
                    : Task.CompletedTask;
            }
        };

        await group.SetupOnAsync(
            Substitute.For<IAdminClient>(), NullLogger.Instance);

        visited.ShouldBe(["one", "two"]);
    }

    [Fact]
    public async Task topic_group_propagates_mixed_results()
    {
        var expected = ExceptionFor(
            ("one", ErrorCode.TopicAlreadyExists, "exists"),
            ("two", ErrorCode.TopicAuthorizationFailed, "denied"));
        var group = new KafkaTopicGroup(
            new KafkaTransport(), ["one", "two"], EndpointRole.Application)
        {
            CreateTopicFunc = (_, _) => Task.FromException(expected)
        };

        var thrown = await Should.ThrowAsync<CreateTopicsException>(
            () => group.SetupOnAsync(
                Substitute.For<IAdminClient>(), NullLogger.Instance).AsTask());

        thrown.ShouldBeSameAs(expected);
    }

    private static Task SetupSingleTopic(CreateTopicsException exception)
    {
        var topic = new KafkaTopic(new KafkaTransport(), "topic", EndpointRole.Application)
        {
            CreateTopicFunc = (_, _) => Task.FromException(exception)
        };

        return topic.SetupOnAsync(
            Substitute.For<IAdminClient>(), NullLogger.Instance).AsTask();
    }

    private static async Task ShouldPropagateFromSingleTopic(CreateTopicsException expected)
    {
        var thrown = await Should.ThrowAsync<CreateTopicsException>(
            () => SetupSingleTopic(expected));

        thrown.ShouldBeSameAs(expected);
    }

    private static CreateTopicsException ExistingTopic(string reason)
    {
        return ExceptionFor(("topic", ErrorCode.TopicAlreadyExists, reason));
    }

    private static CreateTopicsException ExceptionFor(
        params (string Topic, ErrorCode Code, string Reason)[] results)
    {
        return new CreateTopicsException(results.Select(x => new CreateTopicReport
        {
            Topic = x.Topic,
            Error = new Error(x.Code, x.Reason)
        }).ToList());
    }
}
