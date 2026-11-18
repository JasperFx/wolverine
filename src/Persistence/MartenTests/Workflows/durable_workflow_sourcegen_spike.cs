using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Util;
using Wolverine.Workflows;

namespace MartenTests.Workflows;

// GH-5660 SPIKE: [DurableWorkflow] methods compiled into sagas by Wolverine.SourceGeneration, stored with Marten's
// ordinary document saga storage. Every test reads the generated saga's document, which is the whole state of a
// workflow: the step it waits at, what for, the triggering message, the locals that live across a step, and the undo
// messages.
public class durable_workflow_sourcegen_spike : PostgresqlContext, IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private async Task<IHost> startHost()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // The generated sagas are ordinary sagas: conventional discovery would find them too
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PlaceOrderWorkflowSaga>()
                    .IncludeType<ReviewOrderWorkflowSaga>()
                    .IncludeType(typeof(InventoryHandler))
                    .IncludeType(typeof(OrderSinkHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "workflow_sourcegen";
                    m.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine();

                opts.Policies.UseDurableLocalQueues();
                opts.Services.AddSingleton<IShippingLabels, ShippingLabels>();
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    private static async Task stopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static Task<ITrackedSession> sendAsync(IHost host, object message, Guid? sagaId = null)
    {
        return host.TrackActivity().Timeout(30.Seconds())
            .ExecuteAndWaitAsync(c => c.SendAsync(message, new DeliveryOptions { SagaId = sagaId?.ToString() }));
    }

    private static async Task<T> sagaOf<T>(IHost host, Guid id) where T : WorkflowSaga
    {
        await using var session = host.DocumentStore().QuerySession();
        var saga = await session.LoadAsync<T>(id);
        saga.ShouldNotBeNull();
        return saga;
    }

    [Fact]
    public async Task start_then_the_invoke_reply_resumes_it()
    {
        var host = await startHost();
        var id = Guid.NewGuid();

        var tracked = await sendAsync(host, new PlaceOrder(id, "Main Street"));

        // Step 0 sent the request and ended. The reply came back through the saga-id header alone and ran step 1,
        // which went on to WaitForAsync<PaymentReceived> and scheduled its timeout
        tracked.Sent.SingleEnvelope<ReserveInventory>().SagaId.ShouldBe(id.ToString());
        tracked.Received.SingleEnvelope<InventoryReserved>().SagaId.ShouldBe(id.ToString());

        var timeout = tracked.Scheduled.SingleMessage<PlaceOrderWorkflowTimeout>();
        timeout.Step.ShouldBe(2);
        timeout.Delay.ShouldBe(1.Days());

        var saga = await sagaOf<PlaceOrderWorkflowSaga>(host, id);
        saga.Step.ShouldBe(2);
        saga.WaitingFor.ShouldBe(nameof(PaymentReceived));
        saga.Order.ShouldBe(new PlaceOrder(id, "Main Street"));
        saga.Reserved.ShouldBe(new InventoryReserved("Main"));
        saga.Undo.Select(x => x.ToMessage()).ShouldBe(new object[] { new ReleaseInventory(id) });
        saga.IsCompleted().ShouldBeFalse();
    }

    [Fact]
    public async Task wait_for_is_answered()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        await sendAsync(host, new PlaceOrder(id, "Main Street"));

        var tracked = await sendAsync(host, new PaymentReceived(id, 100m));

        // `if (paid is null)` went the other way: the label I/O ran once and the order shipped
        tracked.Sent.SingleMessage<ShipOrder>().Label.ShouldBe("LABEL-MAIN STREET from Main");
        tracked.Sent.MessagesOf<ReserveInventory>().ShouldBeEmpty();
        tracked.Sent.MessagesOf<ReleaseInventory>().ShouldBeEmpty();
        ShippingLabels.Calls[id].ShouldBe(1);

        // Retained for a while after it completed (#5572), so it can still be read
        var saga = await sagaOf<PlaceOrderWorkflowSaga>(host, id);
        saga.Step.ShouldBe(-1);
        saga.WaitingFor.ShouldBeNull();
        saga.CompletedAt.ShouldNotBeNull();
        saga.Status.ShouldBe(SagaStatus.Active);
    }

    [Fact]
    public async Task wait_for_times_out()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        var started = await sendAsync(host, new PlaceOrder(id, "Main Street"));

        // Fire the scheduled timeout now instead of in a day. WaitForAsync returns null, and `if (paid is null)` runs
        var timedOut = await started.PlayScheduledMessagesAsync(30.Seconds());

        timedOut.ExecutionFinished.SingleMessage<PlaceOrderWorkflowTimeout>().Step.ShouldBe(2);
        timedOut.Sent.SingleMessage<ReleaseInventory>().OrderId.ShouldBe(id);
        timedOut.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();

        var saga = await sagaOf<PlaceOrderWorkflowSaga>(host, id);
        saga.Step.ShouldBe(-1);
        saga.CompletedAt.ShouldNotBeNull();

        // A payment after the workflow finished goes to NotFound and changes nothing
        var late = await sendAsync(host, new PaymentReceived(id, 100m));
        late.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();
        ShippingLabels.Calls.ContainsKey(id).ShouldBeFalse();
    }

    [Fact]
    public async Task a_local_assigned_before_a_wait_is_used_after_it()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        InventoryHandler.HeldReplies[id] = true;

        await sendAsync(host, new PlaceOrder(id, "Elm Street"));
        (await sagaOf<PlaceOrderWorkflowSaga>(host, id)).WaitingFor.ShouldBe(nameof(InventoryReserved));

        // `reserved` is assigned in step 1 and read in step 2, so it lives on the saga in between
        await sendAsync(host, new InventoryReserved("East"), id);
        var waiting = await sagaOf<PlaceOrderWorkflowSaga>(host, id);
        waiting.Reserved.ShouldBe(new InventoryReserved("East"));
        waiting.WaitingFor.ShouldBe(nameof(PaymentReceived));

        var tracked = await sendAsync(host, new PaymentReceived(id, 50m));
        tracked.Sent.SingleMessage<ShipOrder>().Label.ShouldBe("LABEL-ELM STREET from East");
    }

    [Fact]
    public async Task a_message_the_workflow_is_not_waiting_for_is_ignored()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        InventoryHandler.HeldReplies[id] = true;
        await sendAsync(host, new PlaceOrder(id, "Ash Street"));

        // The payment comes while the workflow still waits for inventory. The first cut drops it (see the report)
        var early = await sendAsync(host, new PaymentReceived(id, 50m));
        early.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();

        var saga = await sagaOf<PlaceOrderWorkflowSaga>(host, id);
        saga.Step.ShouldBe(1);
        saga.WaitingFor.ShouldBe(nameof(InventoryReserved));
    }

    [Fact]
    public async Task a_wait_inside_an_if_rejoins_the_code_after_the_if()
    {
        var host = await startHost();
        var id = Guid.NewGuid();

        var started = await sendAsync(host, new ReviewOrder(id, 5000m));
        started.Sent.SingleMessage<RequestApproval>().Amount.ShouldBe(5000m);
        (await sagaOf<ReviewOrderWorkflowSaga>(host, id)).WaitingFor.ShouldBe(nameof(ManagerApproved));

        // The rest of the if branch runs, then the code after the if: DelayAsync
        var approved = await sendAsync(host, new ManagerApproved(id, "Jill"));
        approved.Scheduled.SingleMessage<ReviewOrderWorkflowTimeout>().Step.ShouldBe(2);

        var waiting = await sagaOf<ReviewOrderWorkflowSaga>(host, id);
        waiting.Decision.ShouldBe("approved by Jill");
        waiting.WaitingFor.ShouldBe("delay");

        var completed = await approved.PlayScheduledMessagesAsync(30.Seconds());
        completed.Sent.SingleMessage<ReviewCompleted>().Decision.ShouldBe("approved by Jill");
        (await sagaOf<ReviewOrderWorkflowSaga>(host, id)).CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task an_if_with_a_wait_that_does_not_run_goes_straight_to_the_code_after_it()
    {
        var host = await startHost();
        var id = Guid.NewGuid();

        var started = await sendAsync(host, new ReviewOrder(id, 50m));
        started.Sent.MessagesOf<RequestApproval>().ShouldBeEmpty();
        started.Scheduled.SingleMessage<ReviewOrderWorkflowTimeout>().Step.ShouldBe(2);

        var completed = await started.PlayScheduledMessagesAsync(30.Seconds());
        completed.Sent.SingleMessage<ReviewCompleted>().Decision.ShouldBe("approved automatically");
    }

    [Fact]
    public async Task resumes_after_the_host_restarts()
    {
        var first = await startHost();
        var id = Guid.NewGuid();
        await sendAsync(first, new PlaceOrder(id, "Oak Street"));
        (await sagaOf<PlaceOrderWorkflowSaga>(first, id)).WaitingFor.ShouldBe(nameof(PaymentReceived));

        _hosts.Remove(first);
        await stopAsync(first);

        // A brand new host on the same database: nothing about the workflow is in memory, and no task was left waiting
        var second = await startHost();
        var tracked = await sendAsync(second, new PaymentReceived(id, 75m));

        tracked.Sent.SingleMessage<ShipOrder>().Label.ShouldBe("LABEL-OAK STREET from Main");
        tracked.Sent.MessagesOf<ReserveInventory>().ShouldBeEmpty();
        (await sagaOf<PlaceOrderWorkflowSaga>(second, id)).CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task on_failure_undo_messages_are_sent_newest_first_when_the_workflow_fails_for_good()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        await sendAsync(host, new PlaceOrder(id, "nowhere"));

        // The label I/O throws every time: the first attempt plus two retries, each one the whole step
        var tracked = await host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeReceivedAt<ReleaseInventory>(host)
            .SendMessageAndWaitAsync(new PaymentReceived(id, 25m));

        ShippingLabels.Calls[id].ShouldBe(3);
        tracked.MovedToErrorQueue.SingleMessage<PaymentReceived>().OrderId.ShouldBe(id);

        // RefundPayment was registered by the failing step itself, which rolled back, so it reached the compensation
        // on the exception. It was registered last, so it goes first
        tracked.Sent.MessagesOf<object>()
            .Where(x => x is RefundPayment or ReleaseInventory)
            .ShouldBe(new object[] { new RefundPayment(id, 25m), new ReleaseInventory(id) });

        // Both were sent by the saga's compensation handler, in the transaction that also stored its new status
        var compensation = tracked.Received.SingleEnvelope<PlaceOrderWorkflowFailed>();
        tracked.Sent.Envelopes().Where(x => x.Message is RefundPayment or ReleaseInventory)
            .ShouldAllBe(x => x.ConversationId == compensation.Id);

        var saga = await sagaOf<PlaceOrderWorkflowSaga>(host, id);
        saga.Status.ShouldBe(SagaStatus.Compensated);
        saga.CompletedAt.ShouldNotBeNull();
        saga.CompensationLog.Single().ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        saga.CompensationLog.Single().Step.ShouldBe(typeof(PaymentReceived).ToMessageTypeName());
    }
}
