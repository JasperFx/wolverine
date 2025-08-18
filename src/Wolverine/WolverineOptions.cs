using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.MultiTenancy;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Local;

[assembly: InternalsVisibleTo("Wolverine.Testing")]

namespace Wolverine;

public enum MultipleHandlerBehavior
{
    /// <summary>
    /// The "classic" Wolverine behavior where multiple handlers for the same message
    /// type are combined into a single logical message handler
    /// </summary>
    ClassicCombineIntoOneLogicalHandler,
    
    /// <summary>
    /// Force Wolverine to make each individual handler for a message type be
    /// processed completely independently. This is common when you may be publishing
    /// an event message to multiple module handlers within the same process
    /// </summary>
    Separated
}

/// <summary>
///     Completely defines and configures a Wolverine application
/// </summary>
public sealed partial class WolverineOptions
{
    private readonly List<Action<WolverineOptions>> _lazyActions = [];
    private AutoCreate? _autoBuildMessageStorageOnStartup;

    public WolverineOptions() : this(null)
    {
    }

    public WolverineOptions(string? assemblyName)
    {
        Transports = new TransportCollection();

        _serializers.Add(EnvelopeReaderWriter.Instance.ContentType, EnvelopeReaderWriter.Instance);
        _serializers.Add(IntrinsicSerializer.MimeType, IntrinsicSerializer.Instance);

        UseSystemTextJsonForSerialization();

        CodeGeneration = new GenerationRules("Internal.Generated");
        CodeGeneration.Sources.Add(new NowTimeVariableSource());
        CodeGeneration.Sources.Add(new TenantIdSource());
        CodeGeneration.Assemblies.Add(GetType().Assembly);

        if (assemblyName != null)
        {
            establishApplicationAssembly(assemblyName);
        }
        
        if (ApplicationAssembly != null)
        {
            CodeGeneration.Assemblies.Add(ApplicationAssembly);
        }

        Durability = new DurabilitySettings { AssignedNodeNumber = UniqueNodeId.ToString().GetDeterministicHashCode() };

        Policies.Add<SagaPersistenceChainPolicy>();
        Policies.Add<SideEffectPolicy>();
        Policies.Add<ResponsePolicy>();
        Policies.Add<OutgoingMessagesPolicy>();
    }

    /// <summary>
    /// What is the policy within this application for whether or not it is valid to allow Service Location within
    /// the generated code for message handlers or HTTP endpoints. Default is AllowedByWarn. Just keep in mind that
    /// Wolverine really does not want you to use service location if you don't have to!
    ///
    /// Please see https://wolverinefx.net/guide/codegen.html for more information
    /// </summary>
    public ServiceLocationPolicy ServiceLocationPolicy { get; set; } = ServiceLocationPolicy.AllowedButWarn;

    public Uri SubjectUri => new Uri("wolverine://" + ServiceName.Sanitize());

    /// <summary>
    /// How should Wolverine treat message handlers for the same message type?
    /// Default is ClassicCombineIntoOneLogicalHandler, but change this if 
    /// </summary>
    public MultipleHandlerBehavior MultipleHandlerBehavior
    {
        get => HandlerGraph.MultipleHandlerBehavior;
        set => HandlerGraph.MultipleHandlerBehavior = value;
    }

    [IgnoreDescription]
    public Guid UniqueNodeId { get; } = Guid.NewGuid();

    /// <summary>
    ///     Configure or extend how Wolverine does the runtime (or build ahead time) code generation
    /// </summary>
    [ChildDescription]
    public GenerationRules CodeGeneration { get; }

    /// <summary>
    ///     Configure how & where Wolverine discovers message handler classes and message types to override or expand
    ///     the built in conventions. Register additional Wolverine module assemblies
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    [IgnoreDescription]
    public HandlerDiscovery Discovery => HandlerGraph.Discovery;


    /// <summary>
    ///     Options for applying conventional configuration to all or a subset of messaging endpoints
    /// </summary>
    [IgnoreDescription]
    public IPolicies Policies => this;

    /// <summary>
    /// You may want to let Wolverine "know" about a message type upfront that would otherwise
    /// be discovered at runtime so that Wolverine can build in diagnostics or apply message routing
    /// upfront
    /// </summary>
    /// <param name="messageType"></param>
    public void RegisterMessageType(Type messageType)
    {
        HandlerGraph.RegisterMessageType(messageType);
    }

    /// <summary>
    /// </summary>
    [IgnoreDescription]
    public TransportCollection Transports { get; }

    /// <summary>
    ///     Advanced configuration options for Wolverine message processing,
    ///     job scheduling, validation, and resiliency features and node specific settings
    /// </summary>
    [ChildDescription]
    public DurabilitySettings Durability { get; }

    /// <summary>
    ///     The default message execution timeout. This uses a CancellationTokenSource
    ///     behind the scenes, and the timeout enforcement is dependent on the usage within handlers
    /// </summary>
    public TimeSpan DefaultExecutionTimeout { get; set; } = 60.Seconds();


    /// <summary>
    ///     Register additional services to the underlying IoC container with either .NET standard IServiceCollection extension
    ///     methods. This usage will have access to the application's
    ///     full ServiceCollection *at the time of this call*
    /// </summary>
    [IgnoreDescription]
    public IServiceCollection Services { get; internal set; } = new ServiceCollection();

    internal HandlerGraph HandlerGraph { get; } = new();

    /// <summary>
    ///     Direct Wolverine to make any necessary database patches for envelope storage upon
    ///     application start. Default is taken from JasperFxOptions, and is CreateOrDefault 
    /// </summary>
    public AutoCreate AutoBuildMessageStorageOnStartup
    {
        get => _autoBuildMessageStorageOnStartup ?? AutoCreate.CreateOrUpdate;
        set => _autoBuildMessageStorageOnStartup = value;
    }

    /// <summary>
    ///     Descriptive name of the running service. Used in Wolverine diagnostics and testing support
    /// </summary>
    public string ServiceName { get; set; }

    /// <summary>
    ///     This should probably *only* be used in development or testing
    ///     to latch all outgoing message sending
    /// </summary>
    internal bool ExternalTransportsAreStubbed { get; set; }

    [IgnoreDescription]
    internal LocalTransport LocalRouting => Transports.GetOrCreate<LocalTransport>();
    
    internal bool LocalRoutingConventionDisabled { get; set; }

    /// <summary>
    /// Should remote usages of IMessageBus.InvokeAsync() or IMessageBus.InvokeAsync<T>()
    /// that ultimately use an external transport be enabled? Default is true, but you
    /// may want to disable this to avoid surprise network round trips
    /// </summary>
    public bool EnableRemoteInvocation { get; set; } = true;

    /// <summary>
    /// Should message failures automatically try to send a failure acknowledgement message back to the
    /// original caller. Default is *false* as of Wolverine 4.6
    /// </summary>
    public bool EnableAutomaticFailureAcks { get; set; } = false;

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
    
    /// <summary>
    ///     Produce a report of why or why not this Wolverine application
    ///     is finding or not finding methods from this handlerType
    ///     USE THIS TO TROUBLESHOOT HANDLER DISCOVERY ISSUES
    /// </summary>
    /// <param name="handlerType"></param>
    /// <returns></returns>
    public string DescribeHandlerMatch(Type handlerType)
    {
        return Discovery.DescribeHandlerMatch(this, handlerType);
    }

    /// <summary>
    /// Apply a change to this WolverineOptions after all other explicit configurations
    /// are processed. Generally to avoid ordering issues
    /// </summary>
    /// <param name="action"></param>
    public void ConfigureLazily(Action<WolverineOptions> action)
    {
        _lazyActions.Add(action);
    }

    internal void ApplyLazyConfiguration()
    {
        foreach (var action in _lazyActions)
        {
            action(this);
        }

        _lazyActions.Clear();
    }

    public void MapGenericMessageType(Type interfaceType, Type closedType)
    {
        HandlerGraph.MappedGenericMessageTypes[interfaceType] = closedType;
    }

    internal IEnumerable<Endpoint> FindOrCreateEndpointByName(string endpointName)
    {
        var existing = Transports.SelectMany(x => x.Endpoints())
            .Where(x => x.EndpointName.EqualsIgnoreCase(endpointName)).ToArray();

        if (existing.Any()) return existing;

        return [Transports.GetOrCreateEndpoint(new Uri($"local://{endpointName}"))];
    }

    public IEnumerable<Endpoint> FindEndpointsWithHandlerType(Type handlerType)
    {
        return Transports.SelectMany(x => x.Endpoints()).Where(x => x.StickyHandlers.Contains(handlerType));
    }

    public Version? Version => ApplicationAssembly?.GetName().Version ?? Assembly.GetEntryAssembly()?.GetName().Version;
    
    // This helps govern some command line work
    internal bool LightweightMode { get; set; }

    internal void ReadJasperFxOptions(JasperFxOptions jasperfx)
    {
        ServiceName ??= jasperfx.ServiceName;
        
        if (_applicationAssembly == null)
        {
            ApplicationAssembly = jasperfx.ApplicationAssembly;

            if (ApplicationAssembly == null)
            {
                establishApplicationAssembly(null);
            }
        }
        
        if (!CodeGeneration.SourceCodeWritingEnabledHasChanged)
        {
            CodeGeneration.SourceCodeWritingEnabled = jasperfx.ActiveProfile.SourceCodeWritingEnabled;
        }

        if (!CodeGeneration.TypeLoadModeHasChanged)
        {
            CodeGeneration.TypeLoadMode = jasperfx.ActiveProfile.GeneratedCodeMode;
        }

        if (!Durability.TenantIdStyleHasChanged)
        {
            Durability.TenantIdStyle = jasperfx.TenantIdStyle;
        }

        if (_autoBuildMessageStorageOnStartup == null)
        {
            _autoBuildMessageStorageOnStartup = jasperfx.ActiveProfile.ResourceAutoCreate;
        }
    }
    
    public void RegisterMessageType(Type messageType, string messageAlias)
    {
        HandlerGraph.RegisterMessageType(messageType, messageAlias);
    }
}