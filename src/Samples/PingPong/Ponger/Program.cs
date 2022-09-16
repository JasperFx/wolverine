#region sample_PongerBootstrapping

using Wolverine;
using Wolverine.Transports.Tcp;
using Microsoft.Extensions.Hosting;
using Oakton;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        // Using Wolverine's built in TCP transport
        opts.ListenAtPort(5581);
    })
    .RunOaktonCommands(args);


#endregion
