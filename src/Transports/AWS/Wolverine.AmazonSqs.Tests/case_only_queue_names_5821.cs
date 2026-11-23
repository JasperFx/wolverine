using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-5821. An SQS queue name is case-sensitive, but it lives in the host of the endpoint Uri (sqs://Orders), which
/// System.Uri lower cases, so Orders and orders would be two queues with one Wolverine identity. Wolverine refuses to
/// start with such a pair, and does so with the transports stubbed too, so no broker is needed here.
/// </summary>
public class case_only_queue_names_5821
{
    [Fact]
    public async Task two_queues_whose_names_differ_only_in_case_stop_the_host_from_starting()
    {
        var ex = await Should.ThrowAsync<EndpointNameCollisionException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally();
                opts.PublishMessage<SqsCaseOnlyFirstMessage>().ToSqsQueue("Orders5821");
                opts.ListenToSqsQueue("orders5821");
                opts.StubAllExternalTransports();
            }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("sqs://Orders5821");
        ex.Message.ShouldContain("sqs://orders5821");
        ex.Message.ShouldContain("Rename one of them");
    }

    [Fact]
    public async Task one_mixed_case_queue_used_for_publishing_and_listening_is_fine()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseAmazonSqsTransportLocally();
            opts.PublishMessage<SqsCaseOnlyFirstMessage>().ToSqsQueue("Invoices5821");
            opts.ListenToSqsQueue("Invoices5821");
            opts.StubAllExternalTransports();
        }).StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().Options.Transports.OfType<AmazonSqsTransport>().Single().Queues
            .Where(x => x.QueueName.Equals("Invoices5821", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.QueueName)
            .ShouldBe(["Invoices5821"]);
    }
}

public record SqsCaseOnlyFirstMessage;
