using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

// GH-4426 (follow-up to GH-4287 / jasperfx#743): the generated HandlerRegistry manifest also emits
// a Native AOT rooting companion -- a [ModuleInitializer] method carrying [DynamicDependency] roots
// for the registry, every generated handler type, every handler class, every dispatched message
// type, and the closed MessageRouter<T>/EmptyMessageRouter<T> per message type -- so a real
// Native AOT publish under TypeLoadMode.Static no longer needs hand-written rooting.
public class handler_manifest_aot_roots
{
    [Fact]
    public void generated_handler_registry_emits_aot_roots_companion()
    {
        // Same guard as handler_manifest_message_types: the companion is only emitted while
        // actually generating code (codegen write), never during a Static-mode attach.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(AotRootsSampleHandler));
                })
                .Build();

            var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
            var builder = new DynamicCodeBuilder(host.Services, collections)
            {
                ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
            };

            var code = builder.GenerateAllCode();

            code.ShouldContain("public sealed class AotRoots");
            code.ShouldContain("[global::System.Runtime.CompilerServices.ModuleInitializer]");

            // The registry and generated handler executor are sibling generated types, rooted by name.
            code.ShouldContain("typeof(global::Internal.Generated.WolverineHandlers.GeneratedHandlerRegistry)");

            // The handler class and message type, rooted directly.
            code.ShouldContain($"typeof(global::{typeof(AotRootsSampleHandler).FullName})");
            code.ShouldContain($"typeof(global::{typeof(AotRootsSampleMessage).FullName})");

            // The closed generic routers that crash with MissingMethodException under real
            // PublishAot when nothing roots them (GH-4287 / GH-4426).
            code.ShouldContain(
                $"typeof(global::Wolverine.Runtime.Routing.MessageRouter<{typeof(AotRootsSampleMessage).FullName}>)");
            code.ShouldContain(
                $"typeof(global::Wolverine.Runtime.Routing.EmptyMessageRouter<{typeof(AotRootsSampleMessage).FullName}>)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

public record AotRootsSampleMessage;

public static class AotRootsSampleHandler
{
    public static void Handle(AotRootsSampleMessage message)
    {
    }
}
