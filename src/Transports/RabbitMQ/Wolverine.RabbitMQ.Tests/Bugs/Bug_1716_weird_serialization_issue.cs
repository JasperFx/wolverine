using System.Xml;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_1716_weird_serialization_issue
{
    [Fact]
    public async Task try_to_reproduce()
    {
        using var host1 = await startHost();

        var bus = host1.MessageBus();
        await bus.ScheduleAsync(new Bug1716("what"), DateTimeOffset.UtcNow.AddSeconds(15));
        await host1.StopAsync();

        using var host2 = await startHost();
        await Task.Delay(2.Minutes());
    }

    [Fact]
    public void try_to_deserialize_date_string()
    {
        var time = DateTimeOffset.Parse("2025-10-11 21:58:51:345464 Z");
        //var time = XmlConvert.ToDateTime("2025-10-11 21:58:51:345464 Z", XmlDateTimeSerializationMode.Utc);
    }

    private async Task<IHost> startHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gh1716");
                opts.UseRabbitMq().AutoProvision();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.Policies.UseDurableInboxOnAllListeners();

                opts.PublishAllMessages().ToRabbitQueue("gh1716");
                opts.ListenToRabbitQueue("gh1716");
            }).StartAsync();
    }
}

public record Bug1716(string Name);

public static class Bug1716Handler
{
    public static void Handle(Bug1716 message)
    {
        // nothing
    }
}