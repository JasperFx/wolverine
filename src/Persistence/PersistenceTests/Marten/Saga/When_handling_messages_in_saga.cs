using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten.Saga;

public class When_handling_messages_in_saga : PostgresqlContext
{
    [Fact]
    public async Task should_not_throw()
    {
        using var host =
            await Host.CreateDefaultBuilder()
                .UseWolverine()
                .StartAsync();

        var subscriptionId = Guid.NewGuid();


        await host.InvokeMessageAndWaitAsync(
            new Registered(
                "ACME, Inc",
                "Jane",
                "Doe",
                "jd@acme.inc",
                subscriptionId.ToString()
            )
        );
    }
}