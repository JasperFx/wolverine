using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4279. The per-node reply subject was <c>wolverine.response.{node}</c> — nothing in it identified the
/// application. In Balanced mode that is <c>wolverine.response.1</c>, and while
/// <c>AssignedNodeNumber</c> is unique within ONE application's node cluster (the election runs against that
/// application's own message store), two unrelated applications sharing a NATS cluster each elect a node 1.
///
/// <para>The Solo case was already handled — it uses <c>UniqueNodeId</c>, see #3188/#3189 — so this is the
/// Balanced gap only.</para>
///
/// <para>Two failure modes followed. With no queue group (the default) core NATS fans out, so each
/// application received the other's reply payloads: the caller still got its reply, but reply bodies crossed
/// an application boundary. If both applications had set the same <c>DefaultQueueGroup</c>, the two response
/// subscriptions land in one queue group on one subject and NATS load-balances instead — roughly half of one
/// application's replies delivered to the other, and request/reply timeouts for the caller.</para>
///
/// <para>Azure Service Bus, SQS and Redis all put the service name in this name already; RabbitMQ uses a
/// per-process guid. NATS was the only transport with neither.</para>
/// </summary>
public class Bug_4279_reply_subject_carries_the_service_name : IClassFixture<NatsContainerFixture>
{
    private readonly NatsContainerFixture _fixture;

    public Bug_4279_reply_subject_carries_the_service_name(NatsContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task the_reply_subject_names_the_application()
    {
        // Through a real host, because this is the assertion that ConnectAsync actually USES the new
        // naming -- the composition tests below cannot see that. The broker comes from the shared
        // Testcontainers fixture: a hardcoded localhost:4222 works on a developer machine with
        // docker-compose up and fails in CI, where the container gets a dynamic port.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Orders";
                opts.UseNats(_fixture.ConnectionString);
            }).StartAsync(TestContext.Current.CancellationToken);

        var transport = host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.Transports.GetOrCreate<NatsTransport>();

        // The whole point: two applications on one cluster must not share this subject.
        transport.ResponseSubject.ShouldStartWith("wolverine.response.Orders.");
    }

    [Fact]
    public async Task two_applications_that_elected_the_same_node_number_do_not_share_a_reply_subject()
    {
        // The collision needs BALANCED mode and the same assigned node number, which is the ordinary
        // outcome for two applications that each ran their own election against their own message store.
        //
        // Asserting on two default hosts would be vacuous: with no message store they run Solo, and Solo
        // already keys the subject on UniqueNodeId (#3188/#3189), so the subjects differ with or without
        // this fix. AssignedNodeNumber has an internal setter, so the node number is pinned through the
        // transport's own composition instead of the host's.
        const int sameNodeNumber = 1;

        var orders = subjectFor("Orders", sameNodeNumber);
        var billing = subjectFor("Billing", sameNodeNumber);

        orders.ShouldNotBe(billing);
        orders.ShouldBe("wolverine.response.Orders.1");
        billing.ShouldBe("wolverine.response.Billing.1");
    }

    // Mirrors NatsTransport.ConnectAsync's Balanced branch exactly -- the Solo branch is not what collides.
    private static string subjectFor(string serviceName, int assignedNodeNumber)
    {
        return $"wolverine.response.{NatsTransport.sanitizeSubjectToken(serviceName)}.{assignedNodeNumber}";
    }

    [Theory]
    [InlineData("Orders.Api", "Orders_Api")]      // '.' would silently add a subject token
    [InlineData("Orders *", "Orders__")]          // '*' is the single-token wildcard
    [InlineData("Orders>", "Orders_")]            // '>' is the multi-token wildcard
    [InlineData("Order Service", "Order_Service")] // whitespace is not legal in a subject
    [InlineData("Orders", "Orders")]               // and an ordinary name is left alone
    public void a_service_name_is_flattened_into_one_subject_token(string serviceName, string expected)
    {
        // Deliberately not a SanitizeIdentifier override: that runs over every identifier the user names,
        // and '.' is a legal, meaningful separator in a subject they wrote on purpose. Only the value
        // Wolverine splices into a subject it composes needs flattening.
        NatsTransport.sanitizeSubjectToken(serviceName).ShouldBe(expected);
    }


}
