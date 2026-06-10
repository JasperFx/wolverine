using Confluent.Kafka;
using Confluent.Kafka.Admin;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ComplianceTests;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// When a Kafka topic is marked <c>ExternallyOwned()</c> on its listener, subscriber,
/// or topic-group configuration, Wolverine must not attempt to create it during
/// transport startup nor delete it during resource teardown, even when
/// <c>AutoProvision()</c> is enabled on the parent transport. This is the escape
/// hatch for topics owned by an external system where the calling
/// identity lacks <c>CreateTopics</c> or <c>DeleteTopics</c> ACLs.
///
/// As with Bug_2537, the test never produces to the topic so confluent-local's
/// broker-side <c>auto.create.topics.enable=true</c> cannot mask the assertion —
/// we only check broker metadata snapshots before and after the operation.
/// </summary>
public class externally_owned_topics_are_skipped : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _externalListenerTopic = $"ext-listener-{Guid.NewGuid():N}";
    private readonly string _externalPublisherTopic = $"ext-publisher-{Guid.NewGuid():N}";
    private readonly string _externalGroupTopic1 = $"ext-group1-{Guid.NewGuid():N}";
    private readonly string _externalGroupTopic2 = $"ext-group2-{Guid.NewGuid():N}";
    private readonly string _ownedListenerTopic = $"owned-listener-{Guid.NewGuid():N}";

    public externally_owned_topics_are_skipped(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            using var admin = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();
            await admin.DeleteTopicsAsync([
                _externalListenerTopic,
                _externalPublisherTopic,
                _externalGroupTopic1,
                _externalGroupTopic2,
                _ownedListenerTopic,
            ]);
        }
        catch
        {
            // Ignore: topics may never have been created.
        }
    }

    [Fact]
    public async Task externally_owned_listener_topic_is_not_created()
    {
        ListAllTopics().ShouldNotContain(_externalListenerTopic);

        using var host = await StartHost(opts =>
        {
            opts.ListenToKafkaTopic(_externalListenerTopic).ExternallyOwned().ProcessInline();
        });

        ListAllTopics().ShouldNotContain(_externalListenerTopic,
            $"ExternallyOwned() listener topic {_externalListenerTopic} should not have been created");
    }

    [Fact]
    public async Task externally_owned_publisher_topic_is_not_created()
    {
        ListAllTopics().ShouldNotContain(_externalPublisherTopic);

        using var host = await StartHost(opts =>
        {
            opts.PublishMessage<ExternallyOwnedMessage>()
                .ToKafkaTopic(_externalPublisherTopic)
                .ExternallyOwned();
        });

        ListAllTopics().ShouldNotContain(_externalPublisherTopic,
            $"ExternallyOwned() publisher topic {_externalPublisherTopic} should not have been created");
    }

    [Fact]
    public async Task externally_owned_topic_group_topics_are_not_created()
    {
        var topics = ListAllTopics();
        topics.ShouldNotContain(_externalGroupTopic1);
        topics.ShouldNotContain(_externalGroupTopic2);

        using var host = await StartHost(opts =>
        {
            opts.ListenToKafkaTopics(_externalGroupTopic1, _externalGroupTopic2)
                .ExternallyOwned()
                .ProcessInline();
        });

        var after = ListAllTopics();
        after.ShouldNotContain(_externalGroupTopic1,
            $"ExternallyOwned() group topic {_externalGroupTopic1} should not have been created");
        after.ShouldNotContain(_externalGroupTopic2,
            $"ExternallyOwned() group topic {_externalGroupTopic2} should not have been created");
    }

    [Fact]
    public async Task externally_owned_flag_does_not_block_other_topics_from_being_created()
    {
        var topics = ListAllTopics();
        topics.ShouldNotContain(_externalListenerTopic);
        topics.ShouldNotContain(_ownedListenerTopic);

        using var host = await StartHost(opts =>
        {
            // External topic — should be skipped
            opts.ListenToKafkaTopic(_externalListenerTopic).ExternallyOwned().ProcessInline();

            // Owned topic in the same configuration — should still be auto-created
            opts.ListenToKafkaTopic(_ownedListenerTopic).ProcessInline();
        });

        var after = ListAllTopics();
        after.ShouldNotContain(_externalListenerTopic,
            $"ExternallyOwned() topic {_externalListenerTopic} should not have been created");
        after.ShouldContain(_ownedListenerTopic,
            $"Owned topic {_ownedListenerTopic} alongside an ExternallyOwned() topic should still be created");
    }

    [Fact]
    public async Task externally_owned_listener_topic_is_not_deleted_by_teardown()
    {
        await CreateTopicOutOfBand(_externalListenerTopic);
        ListAllTopics().ShouldContain(_externalListenerTopic);

        var exitCode = await BuildHost(opts =>
        {
            opts.ListenToKafkaTopic(_externalListenerTopic).ExternallyOwned().ProcessInline();
        }).RunJasperFxCommands(["resources", "teardown"]);
        exitCode.ShouldBe(0);

        ListAllTopics().ShouldContain(_externalListenerTopic,
            $"ExternallyOwned() listener topic {_externalListenerTopic} should have survived teardown");
    }

    [Fact]
    public async Task externally_owned_publisher_topic_is_not_deleted_by_teardown()
    {
        await CreateTopicOutOfBand(_externalPublisherTopic);
        ListAllTopics().ShouldContain(_externalPublisherTopic);

        var exitCode = await BuildHost(opts =>
        {
            opts.PublishMessage<ExternallyOwnedMessage>()
                .ToKafkaTopic(_externalPublisherTopic)
                .ExternallyOwned();
        }).RunJasperFxCommands(["resources", "teardown"]);
        exitCode.ShouldBe(0);

        ListAllTopics().ShouldContain(_externalPublisherTopic,
            $"ExternallyOwned() publisher topic {_externalPublisherTopic} should have survived teardown");
    }

    [Fact]
    public async Task externally_owned_topic_group_topics_are_not_deleted_by_teardown()
    {
        await CreateTopicOutOfBand(_externalGroupTopic1);
        await CreateTopicOutOfBand(_externalGroupTopic2);
        var before = ListAllTopics();
        before.ShouldContain(_externalGroupTopic1);
        before.ShouldContain(_externalGroupTopic2);

        var exitCode = await BuildHost(opts =>
        {
            opts.ListenToKafkaTopics(_externalGroupTopic1, _externalGroupTopic2)
                .ExternallyOwned()
                .ProcessInline();
        }).RunJasperFxCommands(["resources", "teardown"]);
        exitCode.ShouldBe(0);

        var after = ListAllTopics();
        after.ShouldContain(_externalGroupTopic1,
            $"ExternallyOwned() group topic {_externalGroupTopic1} should have survived teardown");
        after.ShouldContain(_externalGroupTopic2,
            $"ExternallyOwned() group topic {_externalGroupTopic2} should have survived teardown");
    }

    private Task<IHost> StartHost(Action<WolverineOptions> configure)
    {
        return BuildHost(configure).StartAsync();
    }

    private IHostBuilder BuildHost(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c =>
                    {
                        c.AutoOffsetReset = AutoOffsetReset.Earliest;
                        c.GroupId = $"ext-owned-{Guid.NewGuid():N}";
                    });

                configure(opts);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
            });
    }

    private static async Task CreateTopicOutOfBand(string topicName)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();

        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topicName }]);
        }
        catch (CreateTopicsException e) when (e.Message.Contains("already exists."))
        {
            // Fine — pre-existing from a previous failed run.
        }
    }

    /// <summary>
    /// Full-cluster metadata snapshot, which does NOT trigger broker-side
    /// topic auto-creation on confluent-local.
    /// </summary>
    private static string[] ListAllTopics()
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        return metadata.Topics.Select(t => t.Topic).ToArray();
    }
}

public class ExternallyOwnedMessage;
