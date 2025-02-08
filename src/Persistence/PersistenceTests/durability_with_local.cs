using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace PersistenceTests;

public class durability_with_local : PostgresqlContext
{
    [Fact]
    public async Task should_recover_persisted_messages()
    {
        using var host1 = await WolverineHost.ForAsync(opts => opts.ConfigureDurableSender(true, true));
        await host1.SendAsync(new ReceivedMessage());

        var counts = await host1.Get<IMessageStore>().Admin.FetchCountsAsync();

        await host1.StopAsync();

        counts.Incoming.ShouldBe(1);

        // Don't use WolverineHost here because you need the existing persisted state!!!!
        using var host2 = await Host.CreateDefaultBuilder().UseWolverine(opts => opts.ConfigureDurableSender(true, false))
            .StartAsync();
        var messageStore = host2.Get<IMessageStore>();
        var counts2 = await messageStore.Admin.FetchCountsAsync();

        var i = 0;
        while (counts2.Incoming != 1 && i < 100)
        {
            await Task.Delay(100.Milliseconds());
            counts2 = await messageStore.Admin.FetchCountsAsync();
            i++;
        }

        counts2.Incoming.ShouldBe(1);

        await host2.StopAsync();
    }
}

public static class DurableOptionsConfiguration
{
    public static void ConfigureDurableSender(this WolverineOptions opts, bool latched, bool initial)
    {
        if (initial)
        {
            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        }

        opts.PublishAllMessages()
            .ToLocalQueue("one")
            .UseDurableInbox();

        opts.Services.AddMarten(opts =>
            {
                opts.Connection(Servers.PostgresConnectionString);
                opts.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine();

        opts.Services.AddSingleton(new ReceivingSettings { Latched = latched });
    }
}

public class ReceivingSettings
{
    public bool Latched { get; set; } = true;
}

public class ReceivedMessageHandler
{
    public void Handle(ReceivedMessage message, Envelope envelope, ReceivingSettings settings)
    {
        if (settings.Latched)
        {
            throw new DivideByZeroException();
        }
    }

    public static void Configure(HandlerChain chain)
    {
        chain.OnException(e => true).RequeueIndefinitely();
    }
}

public class ReceivedMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
}