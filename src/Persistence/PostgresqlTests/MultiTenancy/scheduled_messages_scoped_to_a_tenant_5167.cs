using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Postgresql;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

// GH-5167. Scheduled messages are stored per database, so ScheduledMessageQuery.TenantId routes query, cancel and
// reschedule to the tenant's database; without a tenant they work against the main database as before
public class scheduled_messages_scoped_to_a_tenant_5167 : MultiTenancyContext
{
    private MultiTenantedMessageStore theStore = null!;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "scheduled_tenants")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
            });

        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Services.AddResourceSetupOnStartup();
    }

    protected override async Task onStartup()
    {
        await theHost.ResetResourceState();
        theStore = theHost.Services.GetRequiredService<IMessageStore>().ShouldBeOfType<MultiTenantedMessageStore>();

        await schedule("red", "booking/1", 2);
        await schedule("red", "booking/2", 1);
        await schedule("blue", "booking/1", 3);
        await schedule(null, "booking/1", 1);
    }

    private async Task schedule(string? tenantId, string groupId, int count)
    {
        var generator = new EnvelopeGenerator { MessageSource = () => new TargetMessage1(Guid.NewGuid(), 1, "Red") };

        for (var i = 0; i < count; i++)
        {
            var envelope = generator.BuildEnvelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
            envelope.GroupId = groupId;

            // null is the main database
            envelope.TenantId = tenantId;
            await theStore.Inbox.StoreIncomingAsync(envelope);
        }
    }

    private Task<ScheduledMessageResults> query(string? tenantId, string? groupId = "booking/1")
    {
        return theStore.ScheduledMessages.QueryAsync(new ScheduledMessageQuery { TenantId = tenantId, GroupId = groupId },
            CancellationToken.None);
    }

    [Fact]
    public async Task query_is_scoped_to_the_tenant_database()
    {
        (await query("red")).TotalCount.ShouldBe(2);
        (await query("blue")).TotalCount.ShouldBe(3);
        (await query("red", null)).TotalCount.ShouldBe(3);

        // No tenant is the main database, as it always was
        (await query(null)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task cancel_is_scoped_to_the_tenant_database()
    {
        await theStore.ScheduledMessages.CancelAsync(new ScheduledMessageQuery { TenantId = "blue", GroupId = "booking/1" },
            CancellationToken.None);

        (await query("blue")).TotalCount.ShouldBe(0);
        (await query("red")).TotalCount.ShouldBe(2);
        (await query(null)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task reschedule_is_scoped_to_the_tenant_database()
    {
        var newTime = DateTimeOffset.UtcNow.AddDays(2);
        await theStore.ScheduledMessages.RescheduleAsync(
            new ScheduledMessageQuery { TenantId = "red", GroupId = "booking/1" }, newTime, CancellationToken.None);

        (await query("red")).Messages.ShouldAllBe(x => x.ScheduledTime!.Value > DateTimeOffset.UtcNow.AddDays(1));
        (await query("red", "booking/2")).Messages.ShouldAllBe(x => x.ScheduledTime!.Value < DateTimeOffset.UtcNow.AddDays(1));
        (await query("blue")).Messages.ShouldAllBe(x => x.ScheduledTime!.Value < DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task naming_only_the_tenant_reschedules_everything_in_its_database()
    {
        var newTime = DateTimeOffset.UtcNow.AddDays(2);
        await theStore.ScheduledMessages.RescheduleAsync(new ScheduledMessageQuery { TenantId = "red" }, newTime,
            CancellationToken.None);

        (await query("red", null)).Messages.ShouldAllBe(x => x.ScheduledTime!.Value > DateTimeOffset.UtcNow.AddDays(1));
        (await query("blue")).Messages.ShouldAllBe(x => x.ScheduledTime!.Value < DateTimeOffset.UtcNow.AddDays(1));
    }
}
