using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Baseline;
using Baseline.Dates;
using BaselineTypeDiscovery;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Wolverine.Transports.Tcp;

[assembly: InternalsVisibleTo("Wolverine.Testing")]

namespace Wolverine;

/// <summary>
///     Completely defines and configures a Wolverine application
/// </summary>
public sealed partial class WolverineOptions
{
    /// <summary>
    /// You may use this to "help" Wolverine in testing scenarios to force
    /// it to consider this assembly as the main application assembly rather
    /// that assuming that the IDE or test runner assembly is the application assembly
    /// </summary>
    public static Assembly? RememberedApplicationAssembly;
    private readonly IList<Type> _extensionTypes = new List<Type>();

    private readonly IDictionary<string, IMessageSerializer>
        _serializers = new Dictionary<string, IMessageSerializer>();

    private IMessageSerializer? _defaultSerializer;
    private readonly AdvancedSettings _advanced;
    private TimeSpan _defaultExecutionTimeout = 60.Seconds();
    private readonly ServiceRegistry _services = new();
    private Assembly? _applicationAssembly;
    private readonly HandlerGraph _handlerGraph = new();
    private readonly List<IWolverineExtension> _appliedExtensions = new();


    public WolverineOptions() : this(null)
    {
    }

    public WolverineOptions(string? assemblyName)
    {
        Transports = new TransportCollection();
        
        _serializers.Add(EnvelopeReaderWriter.Instance.ContentType, EnvelopeReaderWriter.Instance);

        UseNewtonsoftForSerialization();

        establishApplicationAssembly(assemblyName);

        _advanced = new AdvancedSettings(ApplicationAssembly);

        deriveServiceName();

        LocalQueue(TransportConstants.Durable).UseDurableInbox();
    }

    public IPolicies Policies => new EndpointPolicies(Transports);
    
    public TransportCollection Transports { get; }

    /// <summary>
    ///     Advanced configuration options for Wolverine message processing,
    ///     job scheduling, validation, and resiliency features
    /// </summary>
    public AdvancedSettings Advanced => _advanced;

    /// <summary>
    ///     The default message execution timeout. This uses a CancellationTokenSource
    ///     behind the scenes, and the timeout enforcement is dependent on the usage within handlers
    /// </summary>
    public TimeSpan DefaultExecutionTimeout
    {
        get => _defaultExecutionTimeout;
        set => _defaultExecutionTimeout = value;
    }


    /// <summary>
    ///     Register additional services to the underlying IoC container
    /// </summary>
    public ServiceRegistry Services => _services;

    /// <summary>
    ///     The main application assembly for this Wolverine system
    /// </summary>
    public Assembly? ApplicationAssembly
    {
        get => _applicationAssembly;
        set
        {
            _applicationAssembly = value;
            if (Advanced != null)
            {
                Advanced.CodeGeneration.ApplicationAssembly = value;
                Advanced.CodeGeneration.ReferenceAssembly(value);
            }
        }
    }

    internal HandlerGraph HandlerGraph => _handlerGraph;

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

    public IMessageSerializer DefaultSerializer
    {
        get
        {
            return (_defaultSerializer ??=
                _serializers.Values.FirstOrDefault(x => x.ContentType == EnvelopeConstants.JsonContentType) ??
                _serializers.Values.First());
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

    private void establishApplicationAssembly(string? assemblyName)
    {
        if (assemblyName.IsNotEmpty())
        {
            ApplicationAssembly ??= Assembly.Load(assemblyName);
        }
        else if (GetType() == typeof(WolverineOptions) || GetType() == typeof(WolverineOptions))
        {
            if (RememberedApplicationAssembly == null)
            {
                RememberedApplicationAssembly = CallingAssembly.DetermineApplicationAssembly(this);
            }

            ApplicationAssembly ??= RememberedApplicationAssembly;
        }
        else
        {
            ApplicationAssembly ??= CallingAssembly.DetermineApplicationAssembly(this);
        }

        if (ApplicationAssembly == null)
        {
            throw new InvalidOperationException("Unable to determine an application assembly");
        }
    }

    internal List<IWolverineExtension> AppliedExtensions => _appliedExtensions;

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

    public IMessageSerializer FindSerializer(string contentType)
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
}
