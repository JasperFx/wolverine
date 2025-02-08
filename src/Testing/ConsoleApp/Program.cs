#region sample_QuickStartConsoleMain

using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using JasperFx;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Transports.Tcp;

namespace ConsoleApp;

internal class Program
{
    // You may need to enable C# 7.1 or higher for your project
    private static Task<int> Main(string[] args)
    {
        // This bootstraps and runs the Wolverine
        // application as defined by MyAppOptions
        // until the executable is stopped

        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(2222);

                opts.PublishAllMessages().ToPort(2224);

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup()
                    .BindExchange("Main")
                    .ToQueue("queue1", "BKey");

                opts.ListenToRabbitQueue("rabbit1");
                opts.PublishAllMessages().ToRabbitQueue("rabbit2");
            }).RunJasperFxCommands(args);
    }
}

#endregion