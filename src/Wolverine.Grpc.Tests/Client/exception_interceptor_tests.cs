using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Grpc.Client;
using Xunit;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     End-to-end tests for <see cref="WolverineGrpcClientExceptionInterceptor"/>. Each test
///     provokes a server-side fault (via the <see cref="IFaultingService"/> endpoint on the
///     shared fixture) and asserts the client sees a typed .NET exception rather than a bare
///     <see cref="RpcException"/>. Verifies the interceptor handles both unary and streaming
///     shapes, and that the per-client <see cref="WolverineGrpcClientOptions.MapRpcException"/>
///     override takes precedence over the default table.
/// </summary>
[Collection("grpc-client")]
public class exception_interceptor_tests
{
    private readonly WolverineGrpcClientFixture _fixture;

    public exception_interceptor_tests(WolverineGrpcClientFixture fixture)
    {
        _fixture = fixture;
    }

    private ServiceProvider BuildContainer(Action<WolverineGrpcClientOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWolverineGrpcClient<IFaultingService>(o =>
        {
            o.Address = new Uri("http://localhost");
            extra?.Invoke(o);
        }).ConfigureChannel(c => c.HttpHandler = _fixture.ServerHandler);

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("key", typeof(KeyNotFoundException))]
    [InlineData("argument", typeof(ArgumentException))]
    [InlineData("unauthorized", typeof(UnauthorizedAccessException))]
    [InlineData("invalid", typeof(InvalidOperationException))]
    [InlineData("notimpl", typeof(NotImplementedException))]
    [InlineData("timeout", typeof(TimeoutException))]
    public async Task unary_rpc_exception_is_translated_to_typed_dotnet_exception(
        string kind,
        Type expectedType)
    {
        await using var provider = BuildContainer();
        var client = provider.GetRequiredService<IFaultingService>();

        var ex = await Should.ThrowAsync<Exception>(() =>
            client.Throw(new FaultCodeFirstRequest { Kind = kind }));

        ex.ShouldBeAssignableTo(expectedType);
        ex.InnerException.ShouldBeOfType<RpcException>();
    }

    [Fact]
    public async Task unmapped_status_code_surfaces_the_original_rpc_exception()
    {
        // "generic" → StatusCode.Internal — no .NET analog, so the interceptor should return the
        // RpcException unchanged rather than wrapping it in something that loses diagnostic info.
        await using var provider = BuildContainer();
        var client = provider.GetRequiredService<IFaultingService>();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            client.Throw(new FaultCodeFirstRequest { Kind = "generic" }));

        ex.StatusCode.ShouldBe(StatusCode.Internal);
    }

    [Fact]
    public async Task streaming_rpc_exception_after_first_yield_is_translated_per_move_next()
    {
        // Streaming is the shape the propagation + exception interceptors have to treat specially —
        // RpcException fires from MoveNext, not the outer call. Confirms the wrapping
        // MappingStreamReader translates at the right boundary.
        await using var provider = BuildContainer();
        var client = provider.GetRequiredService<IFaultingService>();

        var received = new List<string>();
        var ex = await Should.ThrowAsync<KeyNotFoundException>(async () =>
        {
            await foreach (var reply in client.ThrowStream(new FaultStreamCodeFirstRequest { Kind = "key" }))
            {
                received.Add(reply.Message);
            }
        });

        received.ShouldHaveSingleItem();
        received[0].ShouldBe("about-to-fail");
        ex.InnerException.ShouldBeOfType<RpcException>();
    }

    [Fact]
    public async Task per_client_map_rpc_exception_override_takes_precedence()
    {
        await using var provider = BuildContainer(o =>
        {
            o.MapRpcException = ex => ex.StatusCode == StatusCode.NotFound
                ? new CustomTenantException(ex.Status.Detail, ex)
                : null;
        });
        var client = provider.GetRequiredService<IFaultingService>();

        var ex = await Should.ThrowAsync<CustomTenantException>(() =>
            client.Throw(new FaultCodeFirstRequest { Kind = "key" }));

        ex.InnerException.ShouldBeOfType<RpcException>();
    }

    [Fact]
    public async Task per_client_override_returning_null_falls_through_to_default_table()
    {
        await using var provider = BuildContainer(o =>
        {
            // Override only handles PermissionDenied — NotFound should fall through.
            o.MapRpcException = ex => ex.StatusCode == StatusCode.PermissionDenied
                ? new CustomTenantException(ex.Status.Detail, ex)
                : null;
        });
        var client = provider.GetRequiredService<IFaultingService>();

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            client.Throw(new FaultCodeFirstRequest { Kind = "key" }));
    }

    private sealed class CustomTenantException : Exception
    {
        public CustomTenantException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
