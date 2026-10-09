using IntegrationTests;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// <c>UseDurableOutboxOnAllSendingEndpoints()</c> and <c>UseDurableInboxOnAllListeners()</c> only make an endpoint
/// durable when it supports durable mode, and a NATS endpoint does only when it uses JetStream. Endpoint policies run
/// before an endpoint's own configuration, though, so <c>UseJetStream()</c> had not been applied yet when the policy
/// asked, and every JetStream route and listener silently stayed buffered: no outbox row, no inbox row.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsDurablePolicyTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsDurablePolicyTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task the_durable_policies_reach_jetstream_endpoints()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var stream = $"DURABLEPOLICY_{id}";
        var jetStreamSubject = $"durablepolicy.{id}.js";
        var coreSubject = $"durablepolicy-core.{id}";

        await using (var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString }))
        {
            await connection.CreateJetStreamContext().CreateStreamAsync(
                new StreamConfig(stream, [$"durablepolicy.{id}.>"]), TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DurablePolicy";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nats_durable_policy");
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishMessage<OrderPlaced>().ToNatsSubject(jetStreamSubject).UseJetStream(stream);
                opts.ListenToNatsSubject(jetStreamSubject).UseJetStream(stream, $"durable-policy-{id}");

                // A core NATS route has no durable mode, so the policy leaves it alone
                opts.PublishMessage<DurablePolicyCoreMessage>().ToNatsSubject(coreSubject);

                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.UseDurableInboxOnAllListeners();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();

        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"nats://subject/{jetStreamSubject}")).IsDurable
            .ShouldBeTrue();
        transport.EndpointForSubject(jetStreamSubject).Mode.ShouldBe(EndpointMode.Durable);

        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"nats://subject/{coreSubject}")).IsDurable
            .ShouldBeFalse();
    }
}

public record DurablePolicyCoreMessage(string Id);
