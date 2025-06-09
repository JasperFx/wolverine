// See https://aka.ms/new-console-template for more information

using IntegrationTests;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetailClient;
using RetailClient.Data;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(
        (
            context,
            services
        ) =>
        {
            services.AddMarten(
                options =>
                {
                    options.Connection(Servers.PostgresConnectionString);
                    options.Events.StreamIdentity = StreamIdentity.AsString;
                    options.DisableNpgsqlLogging = true;
                }
            ).InitializeWith(new InitialData(InitialDatasets.Customers)).IntegrateWithWolverine();
        }
    )
    .UseWolverine(
        options =>
        {
            options.UseRabbitMq(
                configure => { configure.ClientProvidedName = "Client"; }
            );
            options.Services.AddHostedService<Worker>();

            options.PublishMessage<PlaceOrder>()
                .ToRabbitQueue("new-orders");
        }
    )
    .Build();

await host.RunAsync();