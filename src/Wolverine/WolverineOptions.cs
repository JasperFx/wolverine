using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Transports.Local;

[assembly: InternalsVisibleTo("Wolverine.Testing")]

namespace Wolverine;

/// <summary>
///     Completely defines and configures a Wolverine application
/// </summary>
public sealed partial class WolverineOptions
{
    public WolverineOptions() : this(null)
    {
        
    }

    public WolverineOptions(string? assemblyName)
    {
        Transports = new TransportCollection();

        _serializers.Add(EnvelopeReaderWriter.Instance.ContentType, EnvelopeReaderWriter.Instance);

        UseNewtonsoftForSerialization();
        
        CodeGeneration = new GenerationRules("Internal.Generated");
        CodeGeneration.Sources.Add(new NowTimeVariableSource());
        CodeGeneration.Assemblies.Add(GetType().GetTypeInfo().Assembly);

        establishApplicationAssembly(assemblyName);
        

        if (ApplicationAssembly != null)
        {
            CodeGeneration.Assemblies.Add(ApplicationAssembly);
        }
        
        Durability = new DurabilitySettings();

        deriveServiceName();

        LocalQueue(TransportConstants.Durable).UseDurableInbox();
    }
    
    
    /// <summary>
    ///  Configure or extend how Wolverine does the runtime (or build ahead time) code generation
    /// </summary>
    public GenerationRules CodeGeneration { get; }


    /// <summary>
    ///     Configure how & where Wolverine discovers message handler classes and message types to override or expand
    ///     the built in conventions. Register additional Wolverine module assemblies
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public HandlerDiscovery Discovery => HandlerGraph.Discovery;


    /// <summary>
    ///     Options for applying conventional configuration to all or a subset of messaging endpoints
    /// </summary>
    public IPolicies Policies => this;

    /// <summary>
    /// </summary>
    public TransportCollection Transports { get; }

    /// <summary>
    ///     Advanced configuration options for Wolverine message processing,
    ///     job scheduling, validation, and resiliency features and node specific settings
    /// </summary>
    public DurabilitySettings Durability { get; }

    /// <summary>
    ///     The default message execution timeout. This uses a CancellationTokenSource
    ///     behind the scenes, and the timeout enforcement is dependent on the usage within handlers
    /// </summary>
    public TimeSpan DefaultExecutionTimeout { get; set; } = 60.Seconds();


    /// <summary>
    ///     Register additional services to the underlying IoC container with either .NET standard IServiceCollection extension
    ///     methods or Lamar's registry DSL syntax . This usage will have access to the application's
    ///     full ServiceCollection *at the time of this call*
    /// </summary>
    public ServiceRegistry Services { get; } = new();

    internal HandlerGraph HandlerGraph { get; } = new();

    /// <summary>
    ///     Direct Wolverine to make any necessary database patches for envelope storage upon
    ///     application start
    /// </summary>
    public bool AutoBuildEnvelopeStorageOnStartup { get; set; }

    internal TypeLoadMode ProductionTypeLoadMode { get; set; }

    /// <summary>
    ///     Descriptive name of the running service. Used in Wolverine diagnostics and testing support
    /// </summary>
    public string ServiceName { get; set; } = Assembly.GetEntryAssembly().GetName().Name;

    /// <summary>
    ///     This should probably *only* be used in development or testing
    ///     to latch all outgoing message sending
    /// </summary>
    internal bool ExternalTransportsAreStubbed { get; set; }

    internal LocalTransport LocalRouting => Transports.GetOrCreate<LocalTransport>();

    private void deriveServiceName()
    {
        if (GetType() == typeof(WolverineOptions))
        {
            ServiceName = ApplicationAssembly?.GetName().Name ?? "WolverineService";
        }
        else
        {
            ServiceName = GetType().Name.Replace("WolverineOptions", "").Replace("Registry", "")
                .Replace("Options", "");
        }
    }

    internal void CombineServices(IServiceCollection services)
    {
        services.Clear();
        services.AddRange(Services);
    }

    /// <summary>
    ///     Automatically rebuild the
    /// </summary>
    public void OptimizeArtifactWorkflow(TypeLoadMode productionMode = TypeLoadMode.Auto)
    {
        ProductionTypeLoadMode = productionMode;
        Services.AddSingleton<IWolverineExtension, OptimizeArtifactWorkflow>();
    }
}