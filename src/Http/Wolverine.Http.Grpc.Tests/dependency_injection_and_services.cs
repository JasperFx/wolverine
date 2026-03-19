using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using Shouldly;
using System.ServiceModel;
using Wolverine.Http.Grpc;
using Wolverine.Runtime;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Tests for dependency injection and service resolution in gRPC endpoints.
/// Verifies that both property injection (via WolverineGrpcEndpointBase) and
/// constructor injection work correctly, and that custom services can be injected.
/// Mirrors patterns from Wolverine.Http.Tests/using_container_services_as_method_arguments.cs.
/// </summary>
public class dependency_injection_and_services : IAsyncLifetime
{
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // Configure Kestrel to use HTTP/2 (required for gRPC)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(dependency_injection_and_services).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
        });

        // Register custom test services
        builder.Services.AddSingleton<IGrpcTestRepository, GrpcTestRepository>();
        builder.Services.AddScoped<IGrpcTestScopedService, GrpcTestScopedService>();

        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapWolverineGrpcEndpoints();

        await _app.StartAsync();

        var serverAddress = _app.Urls.First();
        _channel = GrpcChannel.ForAddress(serverAddress);
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task property_injection_of_message_bus_works_via_base_class()
    {
        var client = _channel!.CreateGrpcService<IDIPropertyInjectionContract>();

        var response = await client.GetDataAsync(
            new DIRequest { Id = "test-id" },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Data.ShouldContain("Bus injected: True");
    }

    [Fact]
    public async Task constructor_injection_of_message_bus_works()
    {
        var client = _channel!.CreateGrpcService<IDIConstructorInjectionContract>();

        var response = await client.GetDataAsync(
            new DIRequest { Id = "ctor-test" },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Data.ShouldContain("Bus injected: True");
    }

    [Fact]
    public async Task constructor_injection_of_custom_singleton_service_works()
    {
        var client = _channel!.CreateGrpcService<IDISingletonServiceContract>();

        var response = await client.GetDataAsync(
            new DIRequest { Id = "singleton-id" },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Data.ShouldBe("Repository data for: singleton-id");
    }

    [Fact]
    public async Task constructor_injection_of_custom_scoped_service_works()
    {
        var client = _channel!.CreateGrpcService<IDIScopedServiceContract>();

        var response = await client.GetDataAsync(
            new DIRequest { Id = "scoped-id" },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Data.ShouldBe("Scoped service: scoped-id");
    }

    [Fact]
    public async Task mixed_constructor_injection_with_multiple_services_works()
    {
        var client = _channel!.CreateGrpcService<IDIMixedServicesContract>();

        var response = await client.GetDataAsync(
            new DIRequest { Id = "mixed-id" },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Data.ShouldContain("Bus: True");
        response.Data.ShouldContain("Repository: True");
        response.Data.ShouldContain("Scoped: True");
    }

    [Fact]
    public async Task scoped_service_is_resolved_per_request()
    {
        var client = _channel!.CreateGrpcService<IDIScopedServiceContract>();

        // Make two calls - each should get a different scoped service instance
        var response1 = await client.GetDataAsync(
            new DIRequest { Id = "request1" },
            CallContext.Default);

        var response2 = await client.GetDataAsync(
            new DIRequest { Id = "request2" },
            CallContext.Default);

        response1.Data.ShouldNotBe(response2.Data); // Different instance IDs appended by scoped service
    }
}

// Test service interfaces and implementations

public interface IGrpcTestRepository
{
    string GetData(string id);
}

public class GrpcTestRepository : IGrpcTestRepository
{
    public string GetData(string id) => $"Repository data for: {id}";
}

public interface IGrpcTestScopedService
{
    string ProcessRequest(string id);
}

public class GrpcTestScopedService : IGrpcTestScopedService
{
    private readonly Guid _instanceId = Guid.NewGuid();

    public string ProcessRequest(string id) => $"Scoped service: {id} (Instance: {_instanceId})";
}

// Test fixtures for DI tests

[ProtoContract]
public class DIRequest
{
    [ProtoMember(1)]
    public string Id { get; set; } = "";
}

[ProtoContract]
public class DIResponse
{
    [ProtoMember(1)]
    public string Data { get; set; } = "";
}

// Service contracts
[ServiceContract]
public interface IDIPropertyInjectionContract
{
    [OperationContract]
    Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default);
}

[ServiceContract]
public interface IDIConstructorInjectionContract
{
    [OperationContract]
    Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default);
}

[ServiceContract]
public interface IDISingletonServiceContract
{
    [OperationContract]
    Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default);
}

[ServiceContract]
public interface IDIScopedServiceContract
{
    [OperationContract]
    Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default);
}

[ServiceContract]
public interface IDIMixedServicesContract
{
    [OperationContract]
    Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default);
}

// Service implementations
[WolverineGrpcService]
public class DIPropertyInjectionService : WolverineGrpcEndpointBase, IDIPropertyInjectionContract
{
    public Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default)
    {
        var data = $"Bus injected: {Bus != null}, Request ID: {request.Id}";
        return Task.FromResult(new DIResponse { Data = data });
    }
}

[WolverineGrpcService]
public class DIConstructorInjectionService : IDIConstructorInjectionContract
{
    private readonly IMessageBus _bus;

    public DIConstructorInjectionService(IMessageBus bus) => _bus = bus;

    public Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default)
    {
        var data = $"Bus injected: {_bus != null}, Request ID: {request.Id}";
        return Task.FromResult(new DIResponse { Data = data });
    }
}

[WolverineGrpcService]
public class DISingletonServiceService : IDISingletonServiceContract
{
    private readonly IGrpcTestRepository _repository;

    public DISingletonServiceService(IGrpcTestRepository repository) => _repository = repository;

    public Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default)
    {
        var data = _repository.GetData(request.Id);
        return Task.FromResult(new DIResponse { Data = data });
    }
}

[WolverineGrpcService]
public class DIScopedServiceService : IDIScopedServiceContract
{
    private readonly IGrpcTestScopedService _scopedService;

    public DIScopedServiceService(IGrpcTestScopedService scopedService) => _scopedService = scopedService;

    public Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default)
    {
        var data = _scopedService.ProcessRequest(request.Id);
        return Task.FromResult(new DIResponse { Data = data });
    }
}

[WolverineGrpcService]
public class DIMixedServicesService : IDIMixedServicesContract
{
    private readonly IMessageBus _bus;
    private readonly IGrpcTestRepository _repository;
    private readonly IGrpcTestScopedService _scopedService;

    public DIMixedServicesService(
        IMessageBus bus,
        IGrpcTestRepository repository,
        IGrpcTestScopedService scopedService)
    {
        _bus = bus;
        _repository = repository;
        _scopedService = scopedService;
    }

    public Task<DIResponse> GetDataAsync(DIRequest request, CallContext context = default)
    {
        var data = $"Bus: {_bus != null}, Repository: {_repository != null}, Scoped: {_scopedService != null}";
        return Task.FromResult(new DIResponse { Data = data });
    }
}
