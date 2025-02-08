#region sample_PongerBootstrapping

using Microsoft.Extensions.Hosting;
using JasperFx;
using Wolverine;
using Wolverine.Transports.Tcp;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Using Wolverine's built in TCP transport
        opts.ListenAtPort(5581);
    })
    .RunJasperFxCommands(args);

#endregion