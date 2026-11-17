using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
/// GH-5167. A message scheduled to a durable local queue inside a Marten session is written to the inbox by the
/// Marten transaction's own insert, which has to carry the GroupId for scheduled message management to find it
/// </summary>
public class scheduled_messages_by_group_id_5167 : IAsyncLifetime
{
    private const string SchemaName = "gh5167_marten";

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5167MartenHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.PublishMessage<Gh5167MartenReminder>().ToLocalQueue("gh5167").UseDurableInbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_message_scheduled_in_a_marten_session_is_found_by_its_group_id()
    {
        await using (var scope = theHost.Services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            outbox.Enroll(session);

            await outbox.ScheduleAsync(new Gh5167MartenReminder("1"), 1.Hours(),
                new DeliveryOptions { GroupId = "booking/1" });
            await outbox.ScheduleAsync(new Gh5167MartenReminder("2"), 1.Hours(),
                new DeliveryOptions { GroupId = "booking/2" });

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var scheduled = theHost.GetRuntime().Storage.ScheduledMessages;

        var booking1 = await scheduled.QueryAsync(new ScheduledMessageQuery { GroupId = "booking/1" },
            CancellationToken.None);
        booking1.TotalCount.ShouldBe(1);
        booking1.Messages.Single().GroupId.ShouldBe("booking/1");

        await scheduled.CancelAsync(new ScheduledMessageQuery { GroupId = "booking/1" }, CancellationToken.None);

        (await scheduled.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None))
            .Messages.Single().GroupId.ShouldBe("booking/2");
    }
}

public record Gh5167MartenReminder(string BookingId);

public static class Gh5167MartenHandler
{
    public static void Handle(Gh5167MartenReminder message)
    {
    }
}
