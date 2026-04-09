using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Diagnostics;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Diagnostics;

/// <summary>
///     Unit and smoke tests for the WolverineDiagnosticsCommand added in GitHub issue #2467.
/// </summary>
public class WolverineDiagnosticsCommandTests
{
    // ── RouteInputToFileName ──────────────────────────────────────────────────

    [Theory]
    [InlineData("POST /api/orders", "POST_api_orders")]
    [InlineData("GET /api/orders/{id}", "GET_api_orders_id")]
    [InlineData("DELETE /api/orders/{id}", "DELETE_api_orders_id")]
    [InlineData("GET /", "GET")]
    [InlineData("PUT /api/v1/users/{userId}/roles", "PUT_api_v1_users_userId_roles")]
    [InlineData("/api/orders", "api_orders")]          // no HTTP method prefix
    [InlineData("POST /api/some-path", "POST_api_some_path")] // hyphens → underscores
    public void route_input_to_file_name(string input, string expected)
    {
        WolverineDiagnosticsCommand.RouteInputToFileName(input).ShouldBe(expected);
    }

    // ── FindHandlerChain — exact and fuzzy matching ──────────────────────────

    [Fact]
    public void find_handler_chain_by_exact_full_name()
    {
        var chains = BuildTestChains();
        var found = WolverineDiagnosticsCommand.FindHandlerChain(
            "CoreTests.Diagnostics.DiagnosticsTestMessage", chains);
        found.ShouldNotBeNull();
        found.MessageType.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public void find_handler_chain_by_short_name()
    {
        var chains = BuildTestChains();
        var found = WolverineDiagnosticsCommand.FindHandlerChain("DiagnosticsTestMessage", chains);
        found.ShouldNotBeNull();
        found.MessageType.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public void find_handler_chain_by_handler_class_name()
    {
        var chains = BuildTestChains();
        var found = WolverineDiagnosticsCommand.FindHandlerChain("DiagnosticsTestHandler", chains);
        found.ShouldNotBeNull();
        found.MessageType.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public void find_handler_chain_fuzzy_contains_message_type()
    {
        var chains = BuildTestChains();
        var found = WolverineDiagnosticsCommand.FindHandlerChain("TestMessage", chains);
        found.ShouldNotBeNull();
        found.MessageType.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public void find_handler_chain_returns_null_for_unknown()
    {
        var chains = BuildTestChains();
        var found = WolverineDiagnosticsCommand.FindHandlerChain("NonExistentXyzHandler", chains);
        found.ShouldBeNull();
    }

    private static HandlerChain[] BuildTestChains()
    {
        // Compile a real HandlerGraph so we get genuine HandlerChain objects.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery
                        .DisableConventionalDiscovery()
                        .IncludeType(typeof(DiagnosticsTestHandler));
                })
                .Build();

            // Accessing ICodeFileCollection triggers HandlerGraph.Compile()
            host.Services.GetServices<ICodeFileCollection>().ToArray();

            var graph = host.Services.GetRequiredService<HandlerGraph>();
            return graph.AllChains().ToArray();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    // ── Full codegen-preview smoke test ──────────────────────────────────────

    [Fact]
    public async Task codegen_preview_generates_code_for_handler()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery
                        .DisableConventionalDiscovery()
                        .IncludeType(typeof(DiagnosticsTestHandler));
                })
                .StartAsync();

            var services = host.Services;
            var serviceVariableSource = services.GetService<JasperFx.CodeGeneration.Model.IServiceVariableSource>();
            var graph = services.GetRequiredService<HandlerGraph>();
            var chains = graph.AllChains().ToArray();

            var chain = WolverineDiagnosticsCommand.FindHandlerChain("DiagnosticsTestMessage", chains);
            chain.ShouldNotBeNull();

            // Mimic what the command does: generate code for a single chain
            var generatedAssembly = graph.StartAssembly(graph.Rules);
            ((JasperFx.CodeGeneration.ICodeFile)chain).AssembleTypes(generatedAssembly);
            var code = generatedAssembly.GenerateCode(serviceVariableSource);

            code.ShouldNotBeNullOrEmpty();
            code.ShouldContain("DiagnosticsTestHandler");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

// ── Test fixtures ────────────────────────────────────────────────────────────

public record DiagnosticsTestMessage(string Text);

public static class DiagnosticsTestHandler
{
    public static void Handle(DiagnosticsTestMessage message)
    {
        // intentionally empty — used only for code-generation testing
    }
}
