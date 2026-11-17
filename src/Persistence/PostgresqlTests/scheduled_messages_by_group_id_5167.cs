using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace PostgresqlTests;

/// <summary>
/// GH-5167. A booking schedules several messages with its business identifier as the GroupId, and the application
/// finds, reschedules and cancels them by that identifier through scheduled message management. One goes to a
/// durable local queue, the other to an endpoint without native scheduling, which Wolverine wraps and keeps in the
/// inbox until it is due
/// </summary>
public class scheduled_messages_by_group_id_5167 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5167Handler));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gh5167");
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.PublishMessage<Gh5167CheckInNotice>().ToPort(PortFinder.GetAvailablePort());

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IScheduledMessages scheduledMessages => theHost.GetRuntime().Storage.ScheduledMessages;

    private Task<ScheduledMessageResults> forBooking(string groupId)
    {
        return scheduledMessages.QueryAsync(new ScheduledMessageQuery { GroupId = groupId }, CancellationToken.None);
    }

    [Fact]
    public async Task find_reschedule_and_cancel_a_bookings_scheduled_messages()
    {
        var bus = theHost.MessageBus();

        await bus.ScheduleAsync(new Gh5167PaymentReminder("1"), 1.Hours(), new DeliveryOptions { GroupId = "booking/1" });
        await bus.ScheduleAsync(new Gh5167CheckInNotice("1"), 2.Hours(), new DeliveryOptions { GroupId = "booking/1" });
        await bus.ScheduleAsync(new Gh5167PaymentReminder("2"), 1.Hours(), new DeliveryOptions { GroupId = "booking/2" });

        var booking1 = await forBooking("booking/1");
        booking1.TotalCount.ShouldBe(2);
        booking1.Messages.ShouldAllBe(x => x.GroupId == "booking/1");

        var newTime = DateTimeOffset.UtcNow.AddDays(3);
        await scheduledMessages.RescheduleAsync(new ScheduledMessageQuery { GroupId = "booking/1" }, newTime,
            CancellationToken.None);

        (await forBooking("booking/1")).Messages
            .ShouldAllBe(x => x.ScheduledTime!.Value > DateTimeOffset.UtcNow.AddDays(2));
        (await forBooking("booking/2")).Messages
            .ShouldAllBe(x => x.ScheduledTime!.Value < DateTimeOffset.UtcNow.AddDays(2));

        await scheduledMessages.CancelAsync(new ScheduledMessageQuery { GroupId = "booking/1" }, CancellationToken.None);

        (await forBooking("booking/1")).TotalCount.ShouldBe(0);
        (await forBooking("booking/2")).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task a_tenant_and_group_id_find_messages_held_for_an_endpoint_without_native_scheduling()
    {
        // In a single database the tenant filter reads the stored envelope, and the message for the TCP endpoint is
        // stored as a wrapper around it
        await theHost.MessageBus().ScheduleAsync(new Gh5167PaymentReminder("1"), 1.Hours(),
            new DeliveryOptions { GroupId = "booking/1", TenantId = "a" });
        await theHost.MessageBus().ScheduleAsync(new Gh5167CheckInNotice("1"), 2.Hours(),
            new DeliveryOptions { GroupId = "booking/1", TenantId = "a" });
        await theHost.MessageBus().ScheduleAsync(new Gh5167CheckInNotice("1"), 2.Hours(),
            new DeliveryOptions { GroupId = "booking/1", TenantId = "b" });

        var results = await scheduledMessages.QueryAsync(
            new ScheduledMessageQuery { GroupId = "booking/1", TenantId = "a" }, CancellationToken.None);

        results.TotalCount.ShouldBe(2);
        results.Messages.ShouldAllBe(x => x.TenantId == "a" && x.GroupId == "booking/1");
    }
}

public record Gh5167PaymentReminder(string BookingId);

public record Gh5167CheckInNotice(string BookingId);

public static class Gh5167Handler
{
    public static void Handle(Gh5167PaymentReminder message)
    {
    }
}
