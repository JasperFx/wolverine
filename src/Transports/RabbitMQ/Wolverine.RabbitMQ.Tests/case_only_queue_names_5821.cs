using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5821. Wolverine now refuses to start with two endpoints whose names differ only in case where they would share one
/// Uri. A RabbitMQ queue name is case-sensitive AND lives in the Uri path (rabbitmq://queue/Orders), which System.Uri
/// keeps as written, so Orders and orders are two queues with two identities and must keep working side by side.
/// </summary>
public class case_only_queue_names_5821
{
    [Fact]
    public async Task two_rabbit_queues_whose_names_differ_only_in_case_are_two_queues()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var pascal = $"Orders5821{suffix}";
        var lower = pascal.ToLowerInvariant();

        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CaseOnlyQueueMessagesHandler>();
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

            opts.PublishMessage<CaseOnlyPascalMessage>().ToRabbitQueue(pascal);
            opts.PublishMessage<CaseOnlyLowerMessage>().ToRabbitQueue(lower);

            opts.ListenToRabbitQueue(pascal);
            opts.ListenToRabbitQueue(lower);
        }).StartAsync(TestContext.Current.CancellationToken);

        var pascalUri = new Uri($"rabbitmq://queue/{pascal}");
        var lowerUri = new Uri($"rabbitmq://queue/{lower}");
        pascalUri.ShouldNotBe(lowerUri);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Endpoints.GetOrBuildSendingAgent(pascalUri)
            .ShouldNotBeSameAs(runtime.Endpoints.GetOrBuildSendingAgent(lowerUri));

        var toPascal = await host.TrackActivity().IncludeExternalTransports().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new CaseOnlyPascalMessage(suffix));
        toPascal.Received.SingleEnvelope<CaseOnlyPascalMessage>().Destination.ShouldBe(pascalUri);

        var toLower = await host.TrackActivity().IncludeExternalTransports().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new CaseOnlyLowerMessage(suffix));
        toLower.Received.SingleEnvelope<CaseOnlyLowerMessage>().Destination.ShouldBe(lowerUri);
    }
}

public record CaseOnlyPascalMessage(string Id);

public record CaseOnlyLowerMessage(string Id);

public class CaseOnlyQueueMessagesHandler
{
    public void Handle(CaseOnlyPascalMessage message)
    {
    }

    public void Handle(CaseOnlyLowerMessage message)
    {
    }
}
