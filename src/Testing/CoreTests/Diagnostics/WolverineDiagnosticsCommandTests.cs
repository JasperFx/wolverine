using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Diagnostics;
using Wolverine.Runtime;
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

    // ── GrpcInputToFileName ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Greeter", "GreeterGrpcHandler")]                    // bare proto service name
    [InlineData("GreeterGrpcService", "GreeterGrpcHandler")]         // stub class name (swap Service → Handler)
    [InlineData("GreeterGrpcHandler", "GreeterGrpcHandler")]         // already-normalized file name
    [InlineData("greetergrpcservice", "greetergrpcHandler")]         // case: preserve input casing on prefix, canonical "Handler"
    [InlineData("greetergrpchandler", "greetergrpchandler")]         // case: already ends with handler (case-insensitive pass-through)
    [InlineData("  Greeter  ", "GreeterGrpcHandler")]                // trims whitespace
    public void grpc_input_to_file_name(string input, string expected)
    {
        WolverineDiagnosticsCommand.GrpcInputToFileName(input).ShouldBe(expected);
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

    // ── FindMessageType ─────────────────────────────────────────────────────────

    [Fact]
    public async Task find_message_type_by_exact_full_name()
    {
        var (messageTypes, graph) = await BuildMessageTypesAsync();
        var found = WolverineDiagnosticsCommand.FindMessageType(
            "CoreTests.Diagnostics.DiagnosticsTestMessage", messageTypes, graph);
        found.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public async Task find_message_type_by_short_name()
    {
        var (messageTypes, graph) = await BuildMessageTypesAsync();
        var found = WolverineDiagnosticsCommand.FindMessageType(
            "DiagnosticsTestMessage", messageTypes, graph);
        found.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public async Task find_message_type_fuzzy_contains()
    {
        var (messageTypes, graph) = await BuildMessageTypesAsync();
        var found = WolverineDiagnosticsCommand.FindMessageType(
            "TestMessage", messageTypes, graph);
        found.ShouldBe(typeof(DiagnosticsTestMessage));
    }

    [Fact]
    public async Task find_message_type_returns_null_for_unknown()
    {
        var (messageTypes, graph) = await BuildMessageTypesAsync();
        var found = WolverineDiagnosticsCommand.FindMessageType(
            "NonExistentXyzMessage", messageTypes, graph);
        found.ShouldBeNull();
    }

    // ── describe-routing smoke tests ─────────────────────────────────────────

    [Fact]
    public async Task describe_routing_handled_message_is_known_to_handler_graph()
    {
        // Note: in MediatorOnly (lightweight) mode, local routing assignments are not populated,
        // so runtime.RoutingFor() returns no routes. We verify discovery and CanHandle instead.
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

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var options = runtime.Options;
            var messageTypes = options.Discovery.FindAllMessages(options.HandlerGraph).ToList();

            var match = WolverineDiagnosticsCommand.FindMessageType(
                "DiagnosticsTestMessage", messageTypes, options.HandlerGraph);

            match.ShouldNotBeNull();
            match.ShouldBe(typeof(DiagnosticsTestMessage));
            options.HandlerGraph.CanHandle(typeof(DiagnosticsTestMessage))
                .ShouldBeTrue("handler graph should know about the handled message type");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task describe_routing_for_unhandled_message_returns_no_routes()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery();
                })
                .StartAsync();

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            WolverineSystemPart.WithinDescription = true;
            try
            {
                var routes = runtime.RoutingFor(typeof(DiagnosticsUnhandledMessage)).Routes;
                routes.ShouldBeEmpty();
            }
            finally
            {
                WolverineSystemPart.WithinDescription = false;
            }
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static async Task<(IReadOnlyList<Type> messageTypes, HandlerGraph graph)> BuildMessageTypesAsync()
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

            var graph = host.Services.GetRequiredService<HandlerGraph>();
            var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
            var messageTypes = options.Discovery.FindAllMessages(graph);
            return (messageTypes, graph);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

// ── Test fixtures ────────────────────────────────────────────────────────────

public record DiagnosticsTestMessage(string Text);

// Unhandled message — no handler registered
public record DiagnosticsUnhandledMessage(string Text);

public static class DiagnosticsTestHandler
{
    public static void Handle(DiagnosticsTestMessage message)
    {
        // intentionally empty — used only for code-generation testing
    }
}
