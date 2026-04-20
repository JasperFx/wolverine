using Confluent.Kafka;
using Confluent.Kafka.Admin;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ComplianceTests;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests.Bugs;

/// <summary>
/// GH-2537: <c>AutoProvision()</c> on the Kafka transport is documented as
/// "Create newly used Kafka topics on endpoint activation if the topic is missing",
/// but on its own it has no effect — the topic never gets created, and the
/// <c>ListenToKafkaTopics(...)</c> group listener then fails with "Subscribed topic
/// not available: &lt;topic&gt;: Broker: Unknown topic or partition". The only
/// workaround today is to additionally call <c>AddResourceSetupOnStartup()</c>,
/// which runs for every broker and bypasses per-transport <c>AutoProvision</c>
/// granularity.
///
/// This test focuses on the group-listener path the issue reporter used
/// (<c>ListenToKafkaTopics</c>). To avoid Confluent's broker-level
/// <c>auto.create.topics.enable=true</c> masking the bug, the test never produces
/// to the topic — it just asserts, after host startup, that the topic exists
/// in a broker metadata snapshot.
///
/// The test deliberately does NOT register <c>AddResourceSetupOnStartup()</c>,
/// so AutoProvision is the only mechanism responsible for creating the topic.
/// </summary>
public class Bug_2537_autoprovision_creates_missing_topics : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _topicName = $"bug2537-{Guid.NewGuid():N}";

    public Bug_2537_autoprovision_creates_missing_topics(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up so reruns start from a blank state, regardless of pass/fail.
        try
        {
            using var admin = new AdminClientBuilder(
                new AdminClientConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();
            await admin.DeleteTopicsAsync([_topicName]);
        }
        catch
        {
            // Ignore: the topic may never have been created.
        }
    }

    [Fact]
    public async Task autoprovision_alone_creates_missing_topic_for_group_listener()
    {
        // Sanity: topic should not yet exist (full metadata snapshot, no per-topic
        // query — that would trigger broker auto-creation on confluent-local).
        ListAllTopics().ShouldNotContain(_topicName,
            $"Test setup error — topic {_topicName} already exists on the broker");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // NO AddResourceSetupOnStartup() — AutoProvision() is the only
                // mechanism that should create the topic here.
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c =>
                    {
                        c.AutoOffsetReset = AutoOffsetReset.Earliest;
                        c.GroupId = $"bug2537-{Guid.NewGuid():N}";
                    });

                opts.ListenToKafkaTopics(_topicName).ProcessInline();

                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
            }).StartAsync();

        // AutoProvision should have created the topic during host startup.
        ListAllTopics().ShouldContain(_topicName,
            $"AutoProvision() should have created topic {_topicName} during host startup");
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
