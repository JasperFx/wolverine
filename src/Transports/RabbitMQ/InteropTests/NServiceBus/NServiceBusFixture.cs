using Microsoft.Extensions.Hosting;
using NServiceBusRabbitMqService;
using Wolverine;
using Wolverine.RabbitMQ;

namespace InteropTests.NServiceBus;

public class NServiceBusFixture : IAsyncLifetime
{
    public IHost NServiceBus { get; private set; }

    public IHost Wolverine { get; private set; }

    public async Task InitializeAsync()
    {
        #region sample_NServiceBus_interoperability

        Wolverine = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRabbitMq()
                .AutoProvision().AutoPurgeOnStartup()
                .BindExchange("wolverine").ToQueue("wolverine")
                .BindExchange("nsb").ToQueue("nsb")
                .BindExchange("NServiceBusRabbitMqService:ResponseMessage").ToQueue("wolverine");

            opts.PublishAllMessages().ToRabbitExchange("nsb")

                // Tell Wolverine to make this endpoint send messages out in a format
                // for NServiceBus
                .UseNServiceBusInterop();

            opts.ListenToRabbitQueue("wolverine")
                .UseNServiceBusInterop()


                .UseForReplies();

            // This facilitates messaging from NServiceBus (or MassTransit) sending as interface
            // types, whereas Wolverine only wants to deal with concrete types
            opts.Policies.RegisterInteropMessageAssembly(typeof(IInterfaceMessage).Assembly);
        }).StartAsync();

        #endregion

        NServiceBus = await Program.CreateHostBuilder(Array.Empty<string>())
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Wolverine.StopAsync();
        await NServiceBus.StopAsync();
    }
}