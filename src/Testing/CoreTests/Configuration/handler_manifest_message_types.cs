using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Configuration;

// GH-2906: the generated HandlerRegistry manifest also captures the conventional message types
// (IMessage implementations + [WolverineMessage]) discovered at `codegen write` time, so that
// message-type discovery — like handler discovery — can skip the assembly scan under
// TypeLoadMode.Static.
public class handler_manifest_message_types
{
    [Fact]
    public void generated_handler_registry_captures_handler_and_message_types()
    {
        // The conventional message-type scan that feeds the manifest is only performed while actually
        // generating code (codegen write); BuildFiles is otherwise enumerated during a Static-mode
        // attach, where scanning would defeat the purpose.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Only our handler produces a chain; IncludeAssembly puts this test assembly into
                    // the message-type scan so the orphan [WolverineMessage] below is discovered.
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(ManifestSampleHandler))
                        .IncludeAssembly(typeof(OrphanManifestMessage).Assembly);
                })
                .Build();

            var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
            var builder = new DynamicCodeBuilder(host.Services, collections)
            {
                ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
            };

            var code = builder.GenerateAllCode();

            // The registry now emits a MessageTypes() accessor alongside HandlerTypes()...
            code.ShouldContain("public override System.Type[] MessageTypes()");

            // ...capturing a conventional [WolverineMessage] type that has NO handler, so it can only
            // have come from the message-type scan being folded into the manifest.
            code.ShouldContain(nameof(OrphanManifestMessage));

            // ...and the handler types are still captured.
            code.ShouldContain(nameof(ManifestSampleHandler));
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

public record ManifestSampleMessage;

public static class ManifestSampleHandler
{
    public static void Handle(ManifestSampleMessage message)
    {
    }
}

// A conventional message type with no handler — discovered only by the IMessage/[WolverineMessage] scan.
[WolverineMessage]
public record OrphanManifestMessage;
