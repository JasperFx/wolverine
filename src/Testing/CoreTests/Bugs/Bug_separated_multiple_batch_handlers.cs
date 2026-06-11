using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

// Companion to Bug_separated_batch_and_single_handler: under MultipleHandlerBehavior.Separated,
// when the batched array type T[] has MULTIPLE Handle(T[]) handlers, Wolverine splits them onto
// per-handler sticky queues. The BatchingProcessor re-enqueues a single produced T[] onto the
// batch's own execution queue, which is none of those sticky queues. Before the fan-out fix this
// threw NoHandlerForEndpointException and no batch handler ran.
public class Bug_separated_multiple_batch_handlers
{
    private static IHostBuilder ConfigureHost(bool withDirectHandler)
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
    public async Task produced_array_fans_out_to_each_sticky_handler_queue()
    {
        using var host = await ConfigureHost(withDirectHandler: false).StartAsync();
        var runtime = host.GetRuntime();

        // The batch's execution queue receives the produced InvoiceEvent[]...
        var batch = runtime.Options.BatchDefinitions.Single();
        var batchUri = new Uri("local://" + batch.LocalExecutionQueueName);
        var batchQueue = runtime.Endpoints.EndpointFor(batchUri)!;

        // ...but the two Handle(InvoiceEvent[]) handlers were separated onto their own sticky queues,
        // so the array must fan out from the batch queue to each of them.
        var handler = runtime.As<IExecutorFactory>().BuildFor(typeof(InvoiceEvent[]), batchQueue)
            .ShouldBeOfType<Executor>().Handler;
        handler.ShouldBeOfType<FanoutMessageHandler<InvoiceEvent[]>>();
    }

    [Fact]
    public async Task separated_multiple_batch_handlers_all_run()
    {
        using var host = await ConfigureHost(withDirectHandler: false).StartAsync();

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
        using var host = await ConfigureHost(withDirectHandler: true).StartAsync();
        var runtime = host.GetRuntime();

        // The direct Handle(InvoiceEvent) collides with the batch element queue, so the batch was
        // moved onto its dedicated -batch queue (Bug_separated_batch_and_single_handler behavior).
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
