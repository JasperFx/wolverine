using IntegrationTests;
using Marten;
using MetricsDemonstrator;
using Oakton.Resources;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Transports.Tcp;

var port1 = PortFinder.GetAvailablePort();
var port2 = PortFinder.GetAvailablePort();
var port3 = PortFinder.GetAvailablePort();

IHost host = Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.Services.AddHostedService<PublishingHostedService>();
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "metrics";
        }).IntegrateWithWolverine();

        opts.ServiceName = "Metrics";

        //opts.Services.AddLogging(x => x.ClearProviders());

        opts.ListenAtPort(port1);
        opts.ListenAtPort(port2);
        opts.ListenAtPort(port3);

        opts.PublishMessage<Message1>().ToPort(port1).UseDurableOutbox();
        opts.PublishMessage<Message2>().ToPort(port1).UseDurableOutbox();
        opts.PublishMessage<Message3>().ToPort(port2).UseDurableOutbox();
        opts.PublishMessage<Message4>().ToPort(port2).UseDurableOutbox();
        opts.PublishMessage<Message5>().ToPort(port3).UseDurableOutbox();
    })
    .UseResourceSetupOnStartup()
    .ConfigureServices(services =>
    {
        services.AddHostedService<PublishingHostedService>();
    })
    .Build();

await host.RunAsync();