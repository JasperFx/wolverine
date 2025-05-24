#region sample_PongerBootstrapping

using Microsoft.Extensions.Hosting;
using Oakton;
using Wolverine;
using Wolverine.Transports.Tcp;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ApplicationAssembly = typeof(Program).Assembly;

        // Using Wolverine's built in TCP transport
        opts.ListenAtPort(5581);
    })
    .RunOaktonCommands(args);

#endregion