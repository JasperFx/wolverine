using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// End-to-end batching behavior under MultipleHandlerBehavior.Separated, where a per-message direct
// handler and a batch handler are independent consumers of the same element type.
//
//   - Direct + batch (LoadEvent): a direct Handle(LoadEvent) and a BatchMessagesOf<LoadEvent>()
//     batch handler must BOTH run. Before the fix the direct handler silently shadowed the batch.
//   - Multiple batch handlers (InvoiceEvent): a batched array type with more than one Handle(T[])
//     handler is split onto per-handler sticky queues; the produced batch must fan out to all of
//     them (previously threw NoHandlerForEndpointException and none ran).
//
// The routing/executor-resolution mechanics behind these are asserted in
// CoreTests.Runtime.Routing.separated_batch_routing.
public class batching_with_separated_handlers
{
    private static IHostBuilder ConfigureMultipleBatchHost(bool withDirectHandler)
    {
        return Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(InvoicePublisher))
                .IncludeType(typeof(InvoiceArchiver));

            if (withDirectHandler)
            {
                opts.Discovery.IncludeType(typeof(InvoiceTelemetry));
            }

            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            opts.BatchMessagesOf<InvoiceEvent>(b => b.TriggerTime = 250.Milliseconds());
        });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100.Milliseconds());
        }
    }

    [Fact]
    public async Task separated_direct_and_batch_handler_both_run_on_local_publish()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(LoadPublisher))
                    .IncludeType(typeof(LoadTelemetry));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.BatchMessagesOf<LoadEvent>(b => b.TriggerTime = 250.Milliseconds());
            })
            .StartAsync();

        LoadPublisher.BatchCalls.Clear();
        LoadTelemetry.SingleCalls.Clear();

        await host.TrackActivity()
            .Timeout(10.Seconds())
            .WaitForMessageToBeReceivedAt<LoadEvent[]>(host)
            .ExecuteAndWaitAsync(c => c.PublishAsync(new LoadEvent(1)));

        // The direct telemetry handler runs per-message...
        LoadTelemetry.SingleCalls.Count.ShouldBe(1);
        // ...and the batch publisher handler also runs on the batched array.
        LoadPublisher.BatchCalls.Count.ShouldBe(1);
        LoadPublisher.BatchCalls[0].Select(x => x.Id).ShouldBe([1]);
    }

    [Fact]
    public async Task separated_multiple_batch_handlers_all_run()
    {
        using var host = await ConfigureMultipleBatchHost(withDirectHandler: false).StartAsync();

        InvoicePublisher.Calls.Clear();
        InvoiceArchiver.Calls.Clear();

        await host.TrackActivity()
            .Timeout(10.Seconds())
            .WaitForMessageToBeReceivedAt<InvoiceEvent[]>(host)
            .ExecuteAndWaitAsync(c => c.PublishAsync(new InvoiceEvent(1)));

        // The array reception condition fires at the batch queue (the fan-out); give the two
        // relayed sticky deliveries time to drain before asserting both handlers ran.
        await WaitForAsync(() => InvoicePublisher.Calls.Count == 1 && InvoiceArchiver.Calls.Count == 1);

        InvoicePublisher.Calls.Count.ShouldBe(1);
        InvoiceArchiver.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task separated_direct_handler_plus_multiple_batch_handlers_all_run()
    {
        using var host = await ConfigureMultipleBatchHost(withDirectHandler: true).StartAsync();
        var runtime = host.GetRuntime();

        // The direct Handle(InvoiceEvent) collides with the batch element queue, so the batch was
        // moved onto its dedicated -batch queue (see separated_batch_routing for the mechanics).
        runtime.Options.BatchDefinitions.Single().LocalExecutionQueueName!.ShouldEndWith("-batch");

        InvoicePublisher.Calls.Clear();
        InvoiceArchiver.Calls.Clear();
        InvoiceTelemetry.Calls.Clear();

        await host.TrackActivity()
            .Timeout(10.Seconds())
            .WaitForMessageToBeReceivedAt<InvoiceEvent[]>(host)
            .ExecuteAndWaitAsync(c => c.PublishAsync(new InvoiceEvent(7)));

        await WaitForAsync(() =>
            InvoiceTelemetry.Calls.Count == 1 && InvoicePublisher.Calls.Count == 1 &&
            InvoiceArchiver.Calls.Count == 1);

        // The direct per-message handler AND both independent batch handlers all run.
        InvoiceTelemetry.Calls.Count.ShouldBe(1);
        InvoicePublisher.Calls.Count.ShouldBe(1);
        InvoiceArchiver.Calls.Count.ShouldBe(1);
    }
}

public record LoadEvent(int Id);

public static class LoadPublisher
{
    public static readonly List<LoadEvent[]> BatchCalls = new();

    public static void Handle(LoadEvent[] messages)
    {
        BatchCalls.Add(messages);
    }
}

public static class LoadTelemetry
{
    public static readonly List<LoadEvent> SingleCalls = new();

    public static void Handle(LoadEvent e)
    {
        SingleCalls.Add(e);
    }
}

public record InvoiceEvent(int Id);

public static class InvoicePublisher
{
    public static readonly List<InvoiceEvent[]> Calls = new();
    public static void Handle(InvoiceEvent[] messages) => Calls.Add(messages);
}

public static class InvoiceArchiver
{
    public static readonly List<InvoiceEvent[]> Calls = new();
    public static void Handle(InvoiceEvent[] messages) => Calls.Add(messages);
}

public static class InvoiceTelemetry
{
    public static readonly List<InvoiceEvent> Calls = new();
    public static void Handle(InvoiceEvent e) => Calls.Add(e);
}
