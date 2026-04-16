using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Smoke tests for GitHub issue #2471: codegen and OpenAPI CLI commands should work
/// without database/transport connectivity.
/// </summary>
public class Bug_2471_codegen_without_connectivity
{
    [Fact]
    public void codegen_preview_works_without_database_connection()
    {
        // Simulate the codegen command setting WithinCodegenCommand = true before building host
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            // Build but do not start the host — the codegen command does this by default
            // (without --start flag). No DB or transport connectivity is needed.
            var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(SimpleCodegenHandler2471));
                })
                .Build();

            var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
            collections.ShouldNotBeEmpty("Expected at least one ICodeFileCollection from Wolverine");

            var builder = new DynamicCodeBuilder(host.Services, collections)
            {
                ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
            };

            // Should not throw even with no real database or transport configured
            var code = builder.GenerateAllCode();
            code.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task host_startup_applies_lightweight_mode_automatically_during_codegen_command()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(SimpleCodegenHandler2471));
                })
                .StartAsync();

            var runtime = host.GetRuntime();

            // Lightweight mode should have been applied automatically
            runtime.Options.LightweightMode.ShouldBeTrue();
            runtime.Options.ExternalTransportsAreStubbed.ShouldBeTrue();
            runtime.Options.Durability.DurabilityAgentEnabled.ShouldBeFalse();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

public record SimpleCodegenMessage2471;

public static class SimpleCodegenHandler2471
{
    public static void Handle(SimpleCodegenMessage2471 message)
    {
    }
}
