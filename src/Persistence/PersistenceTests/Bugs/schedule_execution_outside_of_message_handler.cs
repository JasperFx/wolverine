using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using Xunit;

namespace PersistenceTests.Bugs;

public class schedule_execution_outside_of_message_handler
{
    [Fact]
    public async Task try_it_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");
                opts.Policies.UseDurableLocalQueues();
            }).StartAsync();

        
        var bus = host.MessageBus();
        await bus.ScheduleAsync(new MyGuy("Hey"), 10.Minutes());


        await Task.Delay(1.Minutes());
    }
}

public record MyGuy(string Name);

public static class MyGuyHandler
{
    public static void Handle(MyGuy guy) => Debug.WriteLine("Got my guy " + guy.Name);
}