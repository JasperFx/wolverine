using JasperFx.CodeGeneration;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Manages the collection of gRPC service chains and their code generation.
/// </summary>
public class GrpcGraph : ICodeFileCollection
{
    private readonly List<GrpcChain> _chains = [];
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<GrpcGraph> _logger;

    public GrpcGraph(IWolverineRuntime runtime, ILogger<GrpcGraph> logger)
    {
        _runtime = runtime;
        _logger = logger;
        Rules = runtime.Options.CodeGeneration;
    }

    public IReadOnlyList<GrpcChain> Chains => _chains;

    public GenerationRules Rules { get; }

    public string ChildNamespace => "WolverineGrpcHandlers";

    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        return _chains.Cast<ICodeFile>().ToList();
    }

    /// <summary>
    /// Discovers and registers gRPC service types for code generation.
    /// </summary>
    public void DiscoverServices(IEnumerable<Type> serviceTypes)
    {
        foreach (var serviceType in serviceTypes)
        {
            _logger.LogDebug("Creating GrpcChain for service type: {ServiceType}", serviceType.FullName);
            var chain = new GrpcChain(serviceType);
            _chains.Add(chain);
        }

        _logger.LogInformation("Discovered {Count} gRPC service(s) for code generation", _chains.Count);
    }

    /// <summary>
    /// Gets the generated handler type for a service type.
    /// </summary>
    public Type? GetHandlerType(Type serviceType)
    {
        return _chains.FirstOrDefault(c => c.ServiceType == serviceType)?.HandlerType;
    }
}
