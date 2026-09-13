// AOT smoke test #3 (GH-4287) — see the csproj header for the full story. Boots a Wolverine host
// inside a REAL Native AOT binary through the ordinary public UseWolverine path, dispatches one
// message, and asserts the handler fired. Exit 0 only on the full boot + dispatch.
//
// `codegen write` (or any JasperFx CLI verb) refreshes the committed pre-gen under
// Internal/Generated/ — run it under plain `dotnet run`, never from the native binary.
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Transports.Tcp;

var isCli = args.Length > 0 && args[0] is "codegen" or "describe" or "help" or "?";

var builder = Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ServiceName = "aot-publish-smoke";
        opts.ApplicationAssembly = typeof(AotPublishPingHandler).Assembly;
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AotPublishPingHandler));

        // GH-4232: an EXTERNAL sending endpoint, which is what makes the host build real
        // MessageRoutes at startup -- including routes for the framework's own ISerializable
        // reply types. Closing IntrinsicSerializer<FailureAcknowledgement> reflectively on that
        // path threw MissingMethodException in a native image, so every AOT app with any external
        // transport died at startup. A purely local smoke never builds one of these routes, which
        // is exactly why this shipped unseen past GH-4287. No listener is needed -- nothing is
        // actually sent, the route construction is the thing under test.
        opts.PublishAllMessages().ToPort(59_999);

        if (!isCli)
        {
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
            opts.Services.CritterStackDefaults(cr =>
            {
                cr.Production.AssertAllPreGeneratedTypesExist = true;
                cr.Development.AssertAllPreGeneratedTypesExist = true;
            });
        }
    });

if (isCli)
{
    return await builder.RunJasperFxCommands(args);
}

// GH-4426: there is deliberately NO AotRoots.Pin() call here any more. `codegen write` now emits the
// rooting companion into the committed pre-gen itself, anchored by [ModuleInitializer] -- which is an
// unconditional ILC root, so it needs no call site at all. This smoke passing with nothing hand-written
// IS the assertion that the emitted rooting works.

try
{
    using var host = builder.Build();
    await host.StartAsync();

    var bus = host.Services.GetRequiredService<IMessageBus>();
    await bus.InvokeAsync(new AotPublishPing(42));

    await host.StopAsync();

    if (AotPublishPingHandler.LastValue != 42)
    {
        await Console.Error.WriteLineAsync(
            $"FAIL: the host booted but the handler saw {AotPublishPingHandler.LastValue} instead of 42.");
        return 1;
    }

    Console.WriteLine("OK: Native AOT boot + dispatch smoke passed.");
    return 0;
}
catch (Exception e)
{
    await Console.Error.WriteLineAsync("FAIL: Native AOT boot smoke crashed:");
    await Console.Error.WriteLineAsync(e.ToString());
    return 1;
}

public record AotPublishPing(int Value);

public static class AotPublishPingHandler
{
    public static int LastValue;

    public static void Handle(AotPublishPing message) => LastValue = message.Value;
}

// GH-4426: the hand-written `AotRoots` class that used to live here is GONE, and its deletion is the
// point of this change. It rooted six types by hand -- the generated registry, the generated handler,
// the handler class, the message type, and MessageRouter<T>/EmptyMessageRouter<T> closed over it -- and
// every Native AOT application had to write the same block for every one of its own message types.
// `codegen write` now emits exactly those six roots into Internal/Generated/, so this project asserts
// the emitted version works by having none of its own.
