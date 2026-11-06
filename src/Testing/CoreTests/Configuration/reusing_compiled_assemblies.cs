using JasperFx;
using JasperFx.CodeGeneration;
using Shouldly;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

public record ReuseCompiledMessage(string Name);

public class ReuseCompiledMessageHandler
{
    public static void Handle(ReuseCompiledMessage message)
    {
    }
}

// GH-5378 / jasperfx#1114
[Collection(CodegenCommandCollection.Name)]
public class reusing_compiled_assemblies
{
    private static Task<IHost> startHost(bool reuse, TypeLoadMode mode = TypeLoadMode.Dynamic,
        string? contentRoot = null)
    {
        return buildHost(reuse, mode, contentRoot).StartAsync();
    }

    private static IHostBuilder buildHost(bool reuse, TypeLoadMode mode, string? contentRoot)
    {
        var builder = Host.CreateDefaultBuilder();
        if (contentRoot != null) builder.UseContentRoot(contentRoot);

        return builder.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType<ReuseCompiledMessageHandler>();
            opts.CodeGeneration.TypeLoadMode = mode;
            opts.CodeGeneration.ReuseCompiledAssemblies = reuse;
        });
    }

    private static string newContentRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "wolverine-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Type compiledHandlerType(IHost host)
    {
        var chain = host.GetRuntime().Handlers.ChainFor<ReuseCompiledMessage>()!;
        host.GetRuntime().Handlers.HandlerFor<ReuseCompiledMessage>();
        return chain.Handler!.GetType();
    }

    [Fact]
    public async Task two_hosts_share_the_compiled_handler_assembly_when_on()
    {
        using var one = await startHost(true);
        using var two = await startHost(true);

        compiledHandlerType(one).Assembly.ShouldBeSameAs(compiledHandlerType(two).Assembly);
    }

    [Fact]
    public async Task two_hosts_compile_separately_by_default()
    {
        using var one = await startHost(false);
        using var two = await startHost(false);

        compiledHandlerType(one).Assembly.ShouldNotBeSameAs(compiledHandlerType(two).Assembly);
    }

    [Fact]
    public async Task two_hosts_share_the_compiled_handler_assembly_in_auto_mode()
    {
        var root = newContentRoot();
        try
        {
            using var one = await startHost(true, TypeLoadMode.Auto, root);
            using var two = await startHost(true, TypeLoadMode.Auto, root);

            compiledHandlerType(one).Assembly.ShouldBeSameAs(compiledHandlerType(two).Assembly);

            // and the reused handler still works
            await two.InvokeMessageAndWaitAsync(new ReuseCompiledMessage("hi"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task codegen_test_and_write_commands_succeed_with_the_flag_on_and_hosts_still_share()
    {
        var root = newContentRoot();
        try
        {
            (await buildHost(true, TypeLoadMode.Auto, root).RunJasperFxCommands(["codegen", "test"]))
                .ShouldBe(0);
            (await buildHost(true, TypeLoadMode.Auto, root).RunJasperFxCommands(["codegen", "write"]))
                .ShouldBe(0);

            // The codegen command leaves this process-wide flag set, and while it is set every host starts in
            // MediatorOnly mode -- these two, and every other test's host in this process
            DynamicCodeBuilder.WithinCodegenCommand = false;

            using var one = await startHost(true, TypeLoadMode.Auto, root);
            using var two = await startHost(true, TypeLoadMode.Auto, root);
            compiledHandlerType(one).Assembly.ShouldBeSameAs(compiledHandlerType(two).Assembly);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
            Directory.Delete(root, true);
        }
    }
}

/// <summary>
/// Running a codegen command sets <c>DynamicCodeBuilder.WithinCodegenCommand</c>, a static flag that makes every
/// host started meanwhile come up in MediatorOnly mode, so nothing may run beside these tests
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CodegenCommandCollection
{
    public const string Name = "CodegenCommand";
}
