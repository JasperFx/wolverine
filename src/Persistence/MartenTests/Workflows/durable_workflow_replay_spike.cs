using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using Marten;
using MartenTests.Workflows.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Workflows;

// GH-5654 SPIKE: journal replay for [DurableWorkflow] methods on Marten, proven with the #5405 PlaceOrder example.
// Every test reads the workflow's stream back as a timeline, which doubles as the picture of what the journal holds.
public class durable_workflow_replay_spike : PostgresqlContext, IAsyncLifetime
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

    private async Task<IHost> startHost(params Type[] workflowTypes)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(InventoryHandler))
                    .IncludeType(typeof(OrderSinkHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "workflow_spike";
                    m.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine();

                opts.Policies.UseDurableLocalQueues();
                opts.Services.AddSingleton<IShippingLabels, ShippingLabels>();

                opts.AddDurableWorkflows(workflowTypes.Length == 0 ? [typeof(PlaceOrderWorkflow)] : workflowTypes);
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

    private static async Task<WorkflowState> stateOf(IHost host, Guid id)
    {
        await using var session = host.DocumentStore().QuerySession();
        var state = await session.LoadAsync<WorkflowState>(id);
        state.ShouldNotBeNull();
        return state;
    }

    private static async Task<string[]> timelineOf(IHost host, Guid id)
    {
        await using var session = host.DocumentStore().QuerySession();
        var events = await session.Events.FetchStreamAsync(id);
        return events.Select(describe).ToArray();
    }

    private static string describe(IEvent e)
    {
        var role = e.WorkflowRole();
        return role == null ? e.Data.GetType().Name : $"{role}:{e.Data.GetType().Name}";
    }

    private static readonly string[] WaitingForPayment =
    [
        "WorkflowStarted",
        "trigger:PlaceOrder",
        "WorkflowFailureRegistered",
        "sent:ReserveInventory",
        "received:InventoryReserved",
        "sent:WorkflowTimeout"
    ];

    [Fact]
    public async Task start_then_the_invoke_reply_resumes_it()
    {
        var host = await startHost();
        var id = Guid.NewGuid();

        var tracked = await sendAsync(host, new PlaceOrder(id, "Main Street"));

        // The reply to InvokeAsync came back through the saga-id header and resumed the workflow,
        // which went on to WaitForAsync<PaymentReceived> and scheduled its timeout
        tracked.Sent.SingleMessage<ReserveInventory>().OrderId.ShouldBe(id);
        tracked.Received.SingleEnvelope<InventoryReserved>().SagaId.ShouldBe(id.ToString());
        tracked.Scheduled.SingleMessage<WorkflowTimeout>().WaitingFor.ShouldBe(nameof(PaymentReceived));

        (await timelineOf(host, id)).ShouldBe(WaitingForPayment);

        var state = await stateOf(host, id);
        state.Status.ShouldBe(WorkflowStatus.Waiting);
        state.WaitingFor.ShouldBe(nameof(PaymentReceived));
        state.WorkflowName.ShouldBe("PlaceOrderWorkflow.Run");
        state.WorkflowVersion.ShouldBe(1);
        state.TriggeringMessageType.ShouldBe(typeof(PlaceOrder).FullName);
        state.TriggeringMessage!.ShouldContain(id.ToString());
    }

    [Fact]
    public async Task wait_for_is_answered()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        await sendAsync(host, new PlaceOrder(id, "Main Street"));

        var tracked = await sendAsync(host, new PaymentReceived(id, 100m));

        // The resume read the six events above, replayed four recorded steps with no side effects, then
        // ran the label I/O once and shipped
        WorkflowRunner.LastResumeEventsRead.ShouldBe(6);
        tracked.Sent.SingleMessage<ShipOrder>().Label.ShouldBe("LABEL-MAIN STREET from Main");
        tracked.Sent.MessagesOf<ReserveInventory>().ShouldBeEmpty();
        ShippingLabels.Calls[id].ShouldBe(1);

        (await timelineOf(host, id)).ShouldBe([
            ..WaitingForPayment,
            "received:PaymentReceived",
            "WorkflowFailureRegistered",
            "WorkflowValueRecorded",
            "sent:ShipOrder",
            "WorkflowCompleted"
        ]);

        var state = await stateOf(host, id);
        state.Status.ShouldBe(WorkflowStatus.Completed);
        state.WaitingFor.ShouldBeNull();
    }

    [Fact]
    public async Task wait_for_times_out()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        var started = await sendAsync(host, new PlaceOrder(id, "Main Street"));

        // Fire the scheduled WorkflowTimeout now instead of in a day
        var timedOut = await started.PlayScheduledMessagesAsync(30.Seconds());

        timedOut.Sent.SingleMessage<ReleaseInventory>().OrderId.ShouldBe(id);
        timedOut.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();

        (await timelineOf(host, id)).ShouldBe([
            ..WaitingForPayment,
            "WorkflowTimedOut",
            "sent:ReleaseInventory",
            "WorkflowCompleted"
        ]);

        (await stateOf(host, id)).Status.ShouldBe(WorkflowStatus.Completed);

        // A payment after the workflow finished is a late message and changes nothing
        var late = await sendAsync(host, new PaymentReceived(id, 100m));
        late.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();
        (await timelineOf(host, id)).Length.ShouldBe(WaitingForPayment.Length + 3);
    }

    [Fact]
    public async Task a_message_that_arrives_before_the_workflow_waits_for_it()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        InventoryHandler.HeldReplies[id] = true;

        await sendAsync(host, new PlaceOrder(id, "Elm Street"));
        (await stateOf(host, id)).WaitingFor.ShouldBe(nameof(InventoryReserved));

        // The payment comes in while the workflow is still waiting on inventory. It is stored, not consumed.
        var early = await sendAsync(host, new PaymentReceived(id, 50m));
        early.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();
        (await stateOf(host, id)).WaitingFor.ShouldBe(nameof(InventoryReserved));

        // Now the inventory reply, which only has the saga-id header to find its workflow by. The workflow
        // gets past InvokeAsync, and WaitForAsync<PaymentReceived> is answered at once by the early payment.
        var tracked = await sendAsync(host, new InventoryReserved("East"), id);

        tracked.Sent.SingleMessage<ShipOrder>().Label.ShouldBe("LABEL-ELM STREET from East");
        tracked.Scheduled.MessagesOf<WorkflowTimeout>().ShouldBeEmpty();

        (await timelineOf(host, id)).ShouldBe([
            "WorkflowStarted",
            "trigger:PlaceOrder",
            "WorkflowFailureRegistered",
            "sent:ReserveInventory",
            "received:PaymentReceived",
            "received:InventoryReserved",
            "WorkflowFailureRegistered",
            "WorkflowValueRecorded",
            "sent:ShipOrder",
            "WorkflowCompleted"
        ]);
    }

    [Fact]
    public async Task resumes_from_the_stream_after_a_restart()
    {
        var first = await startHost();
        var id = Guid.NewGuid();
        await sendAsync(first, new PlaceOrder(id, "Oak Street"));
        (await stateOf(first, id)).WaitingFor.ShouldBe(nameof(PaymentReceived));

        _hosts.Remove(first);
        await stopAsync(first);

        // A brand new host on the same database: nothing about the workflow is in memory
        var second = await startHost();
        var tracked = await sendAsync(second, new PaymentReceived(id, 75m));

        tracked.Sent.SingleMessage<ShipOrder>().Label.ShouldBe("LABEL-OAK STREET from Main");
        tracked.Sent.MessagesOf<ReserveInventory>().ShouldBeEmpty();
        (await stateOf(second, id)).Status.ShouldBe(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task a_changed_definition_that_no_longer_matches_the_journal_faults_the_instance()
    {
        var first = await startHost(typeof(PlaceOrderWorkflow));
        var id = Guid.NewGuid();
        InventoryHandler.HeldReplies[id] = true;
        await sendAsync(first, new PlaceOrder(id, "Pine Street"));

        _hosts.Remove(first);
        await stopAsync(first);

        // Redeployed with the workflow changed in place (same name, same version): it now charges the card first
        var second = await startHost(typeof(ChangedPlaceOrderWorkflow));
        var tracked = await second.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.SendAsync(new InventoryReserved("West"),
                new DeliveryOptions { SagaId = id.ToString() }));

        // Nothing was guessed and nothing was sent; the resume message is dead-lettered
        tracked.Sent.MessagesOf<ChargeCard>().ShouldBeEmpty();
        tracked.Sent.MessagesOf<ShipOrder>().ShouldBeEmpty();
        tracked.MovedToErrorQueue.SingleMessage<InventoryReserved>().ShouldNotBeNull();

        var state = await stateOf(second, id);
        state.Status.ShouldBe(WorkflowStatus.Faulted);
        state.FaultReason.ShouldBe(
            $"Workflow PlaceOrderWorkflow.Run v1 ({id}) no longer matches its journal: at step 1 the code sends ChargeCard, " +
            "but the journal recorded a send of ReserveInventory (stream version 4). " +
            "The workflow definition changed under this running instance.");

        (await timelineOf(second, id)).ShouldBe([
            "WorkflowStarted",
            "trigger:PlaceOrder",
            "WorkflowFailureRegistered",
            "sent:ReserveInventory",
            "received:InventoryReserved",
            "WorkflowFaulted"
        ]);
    }

    [Fact]
    public async Task on_failure_undo_messages_are_sent_in_lifo_order_when_the_workflow_fails_for_good()
    {
        var host = await startHost();
        var id = Guid.NewGuid();
        await sendAsync(host, new PlaceOrder(id, "nowhere"));

        // The label I/O throws every time: the first attempt plus two retries, each a full replay
        var tracked = await host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeReceivedAt<ReleaseInventory>(host)
            .SendMessageAndWaitAsync(new PaymentReceived(id, 25m));

        ShippingLabels.Calls[id].ShouldBe(3);
        tracked.MovedToErrorQueue.SingleMessage<PaymentReceived>().OrderId.ShouldBe(id);

        // RefundPayment was registered last (by the failing run itself), so it goes first
        tracked.Sent.MessagesOf<object>()
            .Where(x => x is RefundPayment or ReleaseInventory)
            .Select(x => x.GetType().Name)
            .ShouldBe([nameof(RefundPayment), nameof(ReleaseInventory)]);

        (await timelineOf(host, id)).ShouldBe([
            ..WaitingForPayment,
            "SagaCompensating",
            "undo:RefundPayment",
            "undo:ReleaseInventory",
            "SagaCompensated"
        ]);

        (await stateOf(host, id)).Status.ShouldBe(WorkflowStatus.Compensated);
    }
}
