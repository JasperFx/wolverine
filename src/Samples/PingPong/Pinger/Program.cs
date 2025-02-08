#region sample_BootstrappingPinger

using Messages;
using JasperFx;
using Pinger;
using Wolverine;
using Wolverine.Transports.Tcp;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Using Wolverine's built in TCP transport

        // listen to incoming messages at port 5580
        opts.ListenAtPort(5580);

        // route all Ping messages to port 5581
        opts.PublishMessage<Ping>().ToPort(5581);

        // Registering the hosted service here, but could do
        // that with a separate call to IHostBuilder.ConfigureServices()
        opts.Services.AddHostedService<Worker>();
    })
    .RunJasperFxCommands(args);

#endregion