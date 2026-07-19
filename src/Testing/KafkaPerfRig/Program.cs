using System.Runtime.InteropServices;
using KafkaPerfRig;

var cfg = new RigConfig();
var role = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("RIG_ROLE") ?? "";

switch (role)
{
    case "wolverine-consumer":
        RigHandlerSettings.HandlerMs = cfg.HandlerMs;
        await WolverineConsumer.RunAsync(cfg);
        break;

    case "wolverine-publisher":
        await WolverinePublisher.RunAsync(cfg);
        break;

    case "native-consumer":
    {
        RigHandlerSettings.HandlerMs = cfg.HandlerMs;
        using var cancellation = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            cancellation.Cancel();
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            cancellation.Cancel();
        });
        await NativeTwin.RunConsumerAsync(cfg, cancellation.Token);
        break;
    }

    case "native-publisher":
        await NativeTwin.RunPublisherAsync(cfg);
        break;

    case "rabbit-consumer":
        await WolverineRabbit.RunConsumerAsync(cfg);
        break;

    case "rabbit-publisher":
        await WolverineRabbit.RunPublisherAsync(cfg);
        break;

    case "native-rabbit-consumer":
    {
        RigHandlerSettings.HandlerMs = cfg.HandlerMs;
        using var cancellation = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            cancellation.Cancel();
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            cancellation.Cancel();
        });
        await NativeRabbitTwin.RunConsumerAsync(cfg, cancellation.Token);
        break;
    }

    case "native-rabbit-publisher":
        await NativeRabbitTwin.RunPublisherAsync(cfg);
        break;

    default:
        Console.WriteLine(
            "usage: KafkaPerfRig <wolverine|native|rabbit|native-rabbit>-<consumer|publisher>");
        Console.WriteLine("Configuration via RIG_* environment variables; see RigConfig.cs and rig.sh.");
        return 1;
}

return 0;
