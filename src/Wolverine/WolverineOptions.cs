using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.TypeDiscovery;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

[assembly: InternalsVisibleTo("Wolverine.Testing")]

namespace Wolverine;

/// <summary>
///     Completely defines and configures a Wolverine application
/// </summary>
public sealed partial class WolverineOptions
{
    /// <summary>
    ///     You may use this to "help" Wolverine in testing scenarios to force
    ///     it to consider this assembly as the main application assembly rather
    ///     that assuming that the IDE or test runner assembly is the application assembly
    /// </summary>
    public static Assembly? RememberedApplicationAssembly;

    private readonly IList<Type> _extensionTypes = new List<Type>();

    private readonly IDictionary<string, IMessageSerializer>
        _serializers = new Dictionary<string, IMessageSerializer>();

    private Assembly? _applicationAssembly;

    private IMessageSerializer? _defaultSerializer;


    public WolverineOptions() : this(null)
    {
    }

    public WolverineOptions(string? assemblyName)
    {
        Transports = new TransportCollection();

        _serializers.Add(EnvelopeReaderWriter.Instance.ContentType, EnvelopeReaderWriter.Instance);

        UseNewtonsoftForSerialization();

        establishApplicationAssembly(assemblyName);
        
        Advanced = new AdvancedSettings(ApplicationAssembly);

        deriveServiceName();

        LocalQueue(TransportConstants.Durable).UseDurableInbox();
    }

    /// <summary>
    ///     Options for applying conventional configuration to all or a subset of messaging endpoints
    /// </summary>
    public IEndpointPolicies Policies => new EndpointPolicies(Transports, this);

    /// <summary>
    /// </summary>
    public TransportCollection Transports { get; }

    /// <summary>
    ///     Advanced configuration options for Wolverine message processing,
    ///     job scheduling, validation, and resiliency features
    /// </summary>
    public AdvancedSettings Advanced { get; }

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

    /// <summary>
    ///     The main application assembly for this Wolverine system. You may need or want to explicitly set this in automated
    ///     test harness
    ///     scenarios. Defaults to the application entry assembly
    /// </summary>
    public Assembly? ApplicationAssembly
    {
        get => _applicationAssembly;
        set
        {
            _applicationAssembly = value;

            if (value != null)
            {
                HandlerGraph.Source.Assemblies.Add(value);

                // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                if (Advanced != null)
                {
                    Advanced.CodeGeneration.ApplicationAssembly = value;
                    Advanced.CodeGeneration.ReferenceAssembly(value);
                }
            }
        }
    }

    internal HandlerGraph HandlerGraph { get; } = new();

    /// <summary>
    ///     Options to control how Wolverine discovers message handler actions, error
    ///     handling, local worker queues, and other policies on message handling
    /// </summary>
    public IHandlerConfiguration Handlers => HandlerGraph;

    /// <summary>
    ///     Get or set the logical Wolverine service name. By default, this is
    ///     derived from the name of a custom WolverineOptions
    /// </summary>
    public string? ServiceName
    {
        get => Advanced.ServiceName;
        set => Advanced.ServiceName = value;
    }

    /// <summary>
    ///     Override or get the default message serializer for the application. The default is based around Newtonsoft.Json
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public IMessageSerializer DefaultSerializer
    {
        get
        {
            return _defaultSerializer ??=
                _serializers.Values.FirstOrDefault(x => x.ContentType == EnvelopeConstants.JsonContentType) ??
                _serializers.Values.First();
        }
        set
        {
            if (value == null)
            {
                throw new InvalidOperationException("The DefaultSerializer cannot be null");
            }

            _serializers[value.ContentType] = value;
            _defaultSerializer = value;
        }
    }

    internal List<IWolverineExtension> AppliedExtensions { get; } = new();

    /// <summary>
    ///     Direct Wolverine to make any necessary database patches for envelope storage upon
    ///     application start
    /// </summary>
    public bool AutoBuildEnvelopeStorageOnStartup { get; set; }

    internal TypeLoadMode ProductionTypeLoadMode { get; set; }

    /// <summary>
    ///     All of the assemblies that Wolverine is searching for message handlers and
    ///     other Wolverine items
    /// </summary>
    public IEnumerable<Assembly> Assemblies => HandlerGraph.Source.Assemblies;


    /// <summary>
    ///     Applies the extension to this application
    /// </summary>
    /// <param name="extension"></param>
    public void Include(IWolverineExtension extension)
    {
        ApplyExtensions(new[] { extension });
    }

    /// <summary>
    ///     Applies the extension with optional configuration to the application
    /// </summary>
    /// <param name="configure">Optional configuration of the extension</param>
    /// <typeparam name="T"></typeparam>
    public void Include<T>(Action<T>? configure = null) where T : IWolverineExtension, new()
    {
        var extension = new T();
        configure?.Invoke(extension);

        ApplyExtensions(new IWolverineExtension[] { extension });
    }

    private void deriveServiceName()
    {
        if (GetType() == typeof(WolverineOptions))
        {
            Advanced.ServiceName = ApplicationAssembly?.GetName().Name ?? "WolverineService";
        }
        else
        {
            Advanced.ServiceName = GetType().Name.Replace("WolverineOptions", "").Replace("Registry", "")
                .Replace("Options", "");
        }
    }

    private Assembly? determineCallingAssembly()
    {
        var stack = new StackTrace();
        var frames = stack.GetFrames();
        var wolverine = frames.LastOrDefault(x =>
            x.HasMethod() && x.GetMethod()?.DeclaringType?.Assembly?.GetName().Name == GetType().Assembly.GetName().Name);

        var index = frames.IndexOf(wolverine);
        for (var i = index; i < frames.Length; i++)
        {
            var candidate = frames[i];
            if (candidate.HasMethod())
            {
                var assembly = candidate.GetMethod().DeclaringType.Assembly;
                if (assembly.HasAttribute<WolverineIgnoreAttribute>()) continue;

                if (assembly.GetName().Name.StartsWith("System")) continue;

                return assembly;
            }
        }

        return Assembly.GetEntryAssembly();
    }

    private void establishApplicationAssembly(string? assemblyName)
    {
        if (assemblyName.IsNotEmpty())
        {
            ApplicationAssembly ??= Assembly.Load(assemblyName);
        }
        else if (RememberedApplicationAssembly != null)
        {
            ApplicationAssembly = RememberedApplicationAssembly;
        }
        else
        {
            RememberedApplicationAssembly = ApplicationAssembly = determineCallingAssembly();
        }
        

        if (ApplicationAssembly == null)
        {
            throw new InvalidOperationException("Unable to determine an application assembly");
        }
        else
        {
            HandlerGraph.Source.Assemblies.Fill(ApplicationAssembly);
        }
    }

    internal void ApplyExtensions(IWolverineExtension[] extensions)
    {
        // Apply idempotency
        extensions = extensions.Where(x => !_extensionTypes.Contains(x.GetType())).ToArray();

        foreach (var extension in extensions)
        {
            extension.Configure(this);
            AppliedExtensions.Add(extension);
        }

        _extensionTypes.Fill(extensions.Select(x => x.GetType()));
    }

    internal void CombineServices(IServiceCollection services)
    {
        services.Clear();
        services.AddRange(Services);
    }

    internal IMessageSerializer DetermineSerializer(Envelope envelope)
    {
        if (envelope.ContentType.IsEmpty())
        {
            return DefaultSerializer;
        }

        if (_serializers.TryGetValue(envelope.ContentType, out var serializer))
        {
            return serializer;
        }

        return DefaultSerializer;
    }

    /// <summary>
    ///     Use Newtonsoft.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    public void UseNewtonsoftForSerialization(Action<JsonSerializerSettings>? configuration = null)
    {
        var settings = NewtonsoftSerializer.DefaultSettings();

        configuration?.Invoke(settings);

        var serializer = new NewtonsoftSerializer(settings);

        _serializers[serializer.ContentType] = serializer;
    }

    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    public void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null)
    {
        var options = SystemTextJsonSerializer.DefaultOptions();

        configuration?.Invoke(options);

        var serializer = new SystemTextJsonSerializer(options);

        _serializers[serializer.ContentType] = serializer;
    }

    internal void IncludeExtensionAssemblies(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies) HandlerGraph.Source.IncludeAssembly(assembly);
    }

    internal IMessageSerializer FindSerializer(string contentType)
    {
        if (_serializers.TryGetValue(contentType, out var serializer))
        {
            return serializer;
        }

        throw new ArgumentOutOfRangeException(nameof(contentType));
    }

    internal IMessageSerializer? TryFindSerializer(string contentType)
    {
        if (_serializers.TryGetValue(contentType, out var s))
        {
            return s;
        }

        return null;
    }

    /// <summary>
    ///     Register an alternative serializer with this Wolverine application
    /// </summary>
    /// <param name="serializer"></param>
    public void AddSerializer(IMessageSerializer serializer)
    {
        _serializers[serializer.ContentType] = serializer;
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