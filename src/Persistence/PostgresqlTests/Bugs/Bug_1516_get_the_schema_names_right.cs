using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Bugs;

public class Bug_1516_get_the_schema_names_right : PostgresqlContext
{
    [Fact]
    public async Task get_the_bleeping_schema_names_right()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(o =>
            {
                o.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "ops")
                    .SchemaName("ops")
                    .EnableMessageTransport(x => x.TransportSchemaName("queues").AutoProvision());

                o.PublishAllMessages().ToPostgresqlQueue("outbound");
                o.ListenToPostgresqlQueue("outbound");
            }).StartAsync();

        var tracked = await host.TrackActivity().IncludeExternalTransports().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new TraceMessage { Name = "Tom Landry" });

        tracked.Executed.SingleMessage<TraceMessage>().Name.ShouldBe("Tom Landry");
    }
}

public class TraceMessage
{
    public string Name { get; set; }
}

public class TraceHandler
{
    public void Handle(TraceMessage message)
    {
        Debug.WriteLine("Got message with " + message.Name);
    }
}