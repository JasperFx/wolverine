using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Commands;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ObjectPool;
using JasperFx;
using JasperFx.CodeGeneration.Services;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Heartbeat;

namespace Wolverine;

public enum ExtensionDiscovery
{
    /// <summary>
    /// Wolverine is allowed to try to discover assemblies marked with [WolverineModule] and load Wolverine
    /// extensions
    /// </summary>
    Automatic,
    
    /// <summary>
    /// Extensions must be loaded manually
    /// </summary>
    ManualOnly
}

public static class HostBuilderExtensions
{
    /// <summary>
    ///     Add Wolverine to an ASP.Net Core application with optional configuration to Wolverine
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="overrides">Programmatically configure Wolverine options</param>
    /// <returns></returns>
    public static IHostBuilder UseWolverine(this IHostBuilder builder, Action<WolverineOptions>? overrides = null, ExtensionDiscovery discovery = ExtensionDiscovery.Automatic)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddWolverine(discovery, overrides);
        });
    }

    /// <summary>
    /// Add Wolverine services to your application with automatic extension discovery
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IServiceCollection AddWolverine(this IServiceCollection services, Action<WolverineOptions>? configure)
        => services.AddWolverine(ExtensionDiscovery.Automatic, configure);

    /// <summary>
    /// Add Wolverine services to your application
    /// </summary>
    /// <param name="services"></param>
    /// <param name="discovery">Specify the extension discovery mode</param>
    /// <param name="configure">Apply specific Wolverine configuration for this application</param>
    /// <returns></returns>
    public static IServiceCollection AddWolverine(this IServiceCollection services, ExtensionDiscovery discovery, Action<WolverineOptions>? configure = null)
    {
        var options = new WolverineOptions();
        return AddWolverine(services, options, discovery, configure);
    }

    /// <summary>
    /// Add Wolverine services to your application with a pre-built WolverineOptions object
    /// </summary>
    /// <param name="services"></param>
    /// <param name="options"></param>
    /// <param name="discovery">Specify the extension discovery mode</param>
    /// <param name="configure">Apply specific Wolverine configuration for this application</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "services.AddJasperFx() scans for IJasperFxCommand types via Assembly.GetExportedTypes(). AOT-publishing apps should pre-register commands via the source-generated DiscoveredCommands manifest; the underlying AddJasperFx surface is already documented as requiring trim-safe registration on those code paths.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "JasperFx command bootstrap closes generic List<T> for enumerable arguments. AOT consumers rely on the same source-generated command manifest used by the trim story.")]
    internal static IServiceCollection AddWolverine(this IServiceCollection services, WolverineOptions options,
        ExtensionDiscovery discovery = ExtensionDiscovery.Automatic,
        Action<WolverineOptions>? configure = null)
    {
        if (services.Any(x => x.ServiceType == typeof(IWolverineRuntime)))
        {
            throw new InvalidOperationException(
                "IHostBuilder.UseWolverine() can only be called once per service collection");
        }

        services.AddJasperFx();
        services.AddSingleton<MessageStoreCollection>();

        // The Roslyn runtime compiler (JasperFx.RuntimeCompiler / AssemblyGenerator) is no
        // longer registered by, or referenced from, core WolverineFx. Apps running
        // TypeLoadMode.Dynamic/Auto reference the WolverineFx.RuntimeCompilation package,
        // which auto-registers IAssemblyGenerator via its [WolverineModule] (or an explicit
        // opts.UseRuntimeCompilation() call). TypeLoadMode.Static apps pre-generate all code
        // and ship without Roslyn — smaller binaries, faster cold start, AOT-readiness. A
        // fail-fast guard at startup (WolverineRuntime.HostService.logCodeGenerationConfiguration)
        // catches a Dynamic app that is missing the generator. See #2876 / #1577 / AOT pillar #2746.

        services.AddSingleton(typeof(AncillaryMessageStoreApplication<>));
        
        services.AddSingleton(services);
            
        services.AddSingleton<WolverineSupplementalCodeFiles>();
        services.AddSingleton<ICodeFileCollection>(x => x.GetRequiredService<WolverineSupplementalCodeFiles>());

        services.AddSingleton<IServiceContainer, ServiceContainer>();

        services.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();

        services.AddSingleton(s =>
        {
            var jasperfx = s.GetRequiredService<JasperFxOptions>();

            options.ReadJasperFxOptions(jasperfx);
            
            var extensions = s.GetServices<IWolverineExtension>();
            options.ApplyExtensions(extensions.ToArray());

            var environment = s.GetService<IHostEnvironment>();
            var directory = environment?.ContentRootPath ?? AppContext.BaseDirectory;

            // Don't correct for the path if it's already been set (from JasperFxOptions or user)
            if (options.CodeGeneration.GeneratedCodeOutputPath == "Internal/Generated")
            {
#if DEBUG
                // In DEBUG builds, try to resolve project root like JasperFx does during codegen
                if (jasperfx.AutoResolveProjectRoot)
                {
                    var resolvedRoot = JasperFxOptions.ResolveProjectRoot(directory);
                    if (resolvedRoot != null)
                    {
                        directory = resolvedRoot;
                    }
                }
                else
                {
                    // Legacy behavior for backward compatibility when AutoResolveProjectRoot is false
                    if (directory.EndsWith("Debug", StringComparison.OrdinalIgnoreCase))
                    {
                        directory = directory.ParentDirectory()!.ParentDirectory();
                    }
                    else if (directory.ParentDirectory()!.EndsWith("Debug", StringComparison.OrdinalIgnoreCase))
                    {
                        directory = directory.ParentDirectory()!.ParentDirectory()!.ParentDirectory();
                    }
                }
#endif

                // Use JasperFxOptions path if set, otherwise use the resolved directory
                if (jasperfx.GeneratedCodeOutputPath != null)
                {
                    options.CodeGeneration.GeneratedCodeOutputPath = jasperfx.GeneratedCodeOutputPath;
                }
                else
                {
                    options.CodeGeneration.GeneratedCodeOutputPath =
                        directory!.AppendPath("Internal", "Generated");
                }
            }

            return options;
        });

        services.AddSingleton<IWolverineRuntime, WolverineRuntime>();

        services.AddSingleton<IFaultPublisher>(sp =>
        {
            var wolverineOptions = sp.GetRequiredService<WolverineOptions>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var runtime = sp.GetRequiredService<IWolverineRuntime>();
            return new FaultPublisher(
                wolverineOptions.FaultPublishing,
                runtime,
                loggerFactory.CreateLogger<FaultPublisher>(),
                runtime.Meter);
        });

        services.AddSingleton<ISystemPart, WolverineSystemPart>();

        services.AddSingleton(options.HandlerGraph);
        services.AddSingleton(options.Durability);
        
        // The runtime is also a hosted service
        services.AddSingleton(s => (IHostedService)s.GetRequiredService<IWolverineRuntime>());

        // Lightweight Solo liveness bookends for a storeless Solo host (#3188). Registered AFTER
        // the runtime hosted service so NodeStarted() fires once transports are up, and (hosted
        // services stop in reverse) NodeStopped() fires before the runtime tears them down.
        // No-ops unless the host is Solo with a NullMessageStore.
        services.AddSingleton<SoloHeartbeatService>();
        services.AddSingleton(s => (IHostedService)s.GetRequiredService<SoloHeartbeatService>());

        services.MessagingRootService(x => x.MessageTracking);

        services.AddSingleton<InMemorySagaPersistor>();

        services.MessagingRootService(x => x.Pipeline);

        services.AddOptions();
        services.AddLogging();

        // GH-3001: structural scope priming. When a handler falls back to service location, the
        // generated code creates a child scope and primes its ScopedMessageContextHolder with the
        // handler's MessageContext (PrimeScopedMessageContextFrame). These factories prefer that
        // primed instance, so a service-located IMessageContext / IMessageBus is the SAME context the
        // handler uses (enrolled with the active outbox) rather than a duplicate. Non-handler scopes
        // (hosted services, admin tools, raw resolution) leave the holder empty and fall back to a
        // fresh MessageContext. Replaces the AsyncLocal MessageContext.Current handoff (GH-2583).
        services.AddScoped<ScopedMessageContextHolder>();
        services.AddScoped<IMessageBus>(sp =>
            sp.GetRequiredService<ScopedMessageContextHolder>().Context ?? sp.GetRequiredService<MessageContext>());
        services.AddScoped<IMessageContext>(sp =>
            sp.GetRequiredService<ScopedMessageContextHolder>().Context ?? sp.GetRequiredService<MessageContext>());
        services.AddScoped<MessageContext>();

        services.AddSingleton<ObjectPoolProvider>(new DefaultObjectPoolProvider());

        // I'm not proud of this code, but you need a non-null
        // Container property to use the codegen
        services.AddSingleton<ICodeFileCollection>(c =>
        {
            var handlers = c.GetRequiredService<HandlerGraph>();
            var container = c.GetRequiredService<IServiceContainer>();
            handlers.Container = container;

            // Ugly workaround. Leave this be.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (handlers.Rules == null)
            {
                handlers.Compile(container.GetInstance<WolverineOptions>(), container);
            }

            handlers.Rules ??= c.GetRequiredService<WolverineOptions>().CodeGeneration;

            return handlers;
        });

        options.Services = services;
        if (discovery == ExtensionDiscovery.Automatic)
        {
            options.DiscoverAndApplyExtensions();
        }

        if (options.ApplicationAssembly != null)
        {
            options.HandlerGraph.Discovery.Assemblies.Fill(options.ApplicationAssembly);
        }

        configure?.Invoke(options);

        if (options.Discovery.IncludeHandlerModules)
        {
            options.HandlerGraph.Discovery.DiscoverHandlerModules(options.ApplicationAssembly);
        }

        options.ApplyLazyConfiguration();

        // JasperFx's inline IEnumerable<T> codegen needs keyed "mirror" singletons registered for any
        // service family that mixes a singleton with non-singleton registrations (e.g. one AddSingleton
        // + one AddScoped of the same interface). The generated code injects the singleton element via
        // [FromKeyedServices(key)]; without the mirror the singleton element resolves as null at runtime
        // (resolves #2896). The mirror keys are ordinal-based, so this MUST run as the last registration
        // step Wolverine controls — after the configure callback and ApplyLazyConfiguration, and before
        // the host builds the service provider. It is idempotent and only touches mixed-lifetime families.
        services.AddJasperFxEnumerableSingletonSupport();

        return services;
    }

    /// <summary>
    /// Bootstrap Wolverine into a HostApplicationBuilder
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IHostApplicationBuilder UseWolverine(this IHostApplicationBuilder builder,
        Action<WolverineOptions>? configure)
    {
        builder.Services.AddWolverine(configure);

        return builder;
    }

    internal static void MessagingRootService<T>(this IServiceCollection services,
        Func<IWolverineRuntime, T> expression)
        where T : class
    {
        services.AddSingleton(s => expression(s.GetRequiredService<IWolverineRuntime>()));
    }

    /// <summary>
    /// Create a new message bus instance directly from the IHost. Helpful for testing
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    public static IMessageBus MessageBus(this IHost host)
    {
        return new MessageBus(host.Services.GetRequiredService<IWolverineRuntime>());
    }

    /// <summary>
    ///     Syntactical sugar to execute the Wolverine command line for a configured WebHostBuilder
    /// </summary>
    /// <param name="hostBuilder"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode(
        "Dispatches to JasperFx commands resolved reflectively from the entry/extension assemblies. " +
        "AOT-publishing apps should pre-register commands via the source-generated DiscoveredCommands manifest.")]
    [RequiresDynamicCode(
        "Command input parsing closes generic List<T> via MakeGenericType for enumerable arguments / flags.")]
    public static Task<int> RunWolverineAsync(this IHostBuilder hostBuilder, string[] args)
    {
        return hostBuilder.RunJasperFxCommands(args);
    }

    public static T Get<T>(this IHost host) where T : notnull
    {
        return host.Services.GetRequiredService<T>();
    }

    public static object Get(this IHost host, Type serviceType)
    {
        return host.Services.GetRequiredService(serviceType);
    }

    /// <summary>
    ///     Syntactical sugar for host.Services.GetRequiredService<IMessagePublisher>().Send(message)
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ValueTask SendAsync<T>(this IHost host, T message, DeliveryOptions? options = null)
    {
        return host.MessageBus().SendAsync(message, options);
    }

    /// <summary>
    ///     Syntactical sugar for host.Services.GetRequiredService<IMessagePublisher>().Send(message)
    /// </summary>
    /// <param name="host"></param>
    /// <param name="endpointName"></param>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ValueTask SendToEndpointAsync<T>(this IHost host, string endpointName, T message,
        DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return host.MessageBus().EndpointFor(endpointName).SendAsync(message, options);
    }

    /// <summary>
    ///     Syntactical sugar to invoke a single message with the registered
    ///     Wolverine command bus for this host
    /// </summary>
    /// <param name="host"></param>
    /// <param name="command"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Task InvokeAsync<T>(this IHost host, T command)
    {
        return host.MessageBus().InvokeAsync(command!);
    }

    /// <summary>
    /// Add a wolverine extension to the IoC container to apply extra configuration to your system
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddWolverineExtension<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services) where T : class, IWolverineExtension
    {
        return services.AddSingleton<IWolverineExtension, T>();
    }

    /// <summary>
    /// Add an asynchronous wolverine extension to the IoC container to apply extra configuration to your system
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddAsyncWolverineExtension<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services) where T : class, IAsyncWolverineExtension
    {
        return services.AddSingleton<IAsyncWolverineExtension, T>();
    }

    /// <summary>
    /// Apply all asynchronous Wolverine configuration extensions to the Wolverine application.
    /// This is necessary if you are using Wolverine.HTTP endpoints
    /// </summary>
    /// <param name="services"></param>
    public static Task ApplyAsyncWolverineExtensions(this IServiceProvider services)
    {
        return services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>().ApplyAsyncExtensions();
    }

    /// <summary>
    /// Registers a SingularAgent type to this Wolverine system
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddSingularAgent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services) where T : SingularAgent
    {
        services.AddSingleton<IAgentFamily, T>();
        return services;
    }

    /// <summary>
    ///     Disable all Wolverine messaging outside the current process. This is almost entirely
    ///     meant to enable integration testing scenarios where you only mean to execute messages
    ///     locally.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>

    #region sample_extension_method_to_disable_external_transports
    public static IServiceCollection DisableAllExternalWolverineTransports(this IServiceCollection services)
    {
        services.AddSingleton<IWolverineExtension, DisableExternalTransports>();
        return services;
    }

    #endregion
    
    /// <summary>
    /// Disable all Wolverine message persistence bootstrapping and durability agents. This
    /// was built for the case of needing to run the application for OpenAPI generation when
    /// the database might not be available
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection DisableAllWolverineMessagePersistence(this IServiceCollection services)
    {
        services.AddSingleton<IWolverineExtension, DisablePersistence>();
        services.RemoveAll<IMessageStore>();
        services.RemoveAll<AncillaryMessageStore>();
        return services;
    }

    #region sample_disableexternaltransports
    internal class DisableExternalTransports : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            options.ExternalTransportsAreStubbed = true;
        }
    }

    #endregion
    
    internal class DisablePersistence : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            options.Durability.DurabilityMetricsEnabled = false;
            options.Durability.DurabilityAgentEnabled = false;
        }
    }
    
    /// <summary>
    /// Override the durability mode of Wolverine to be "Solo". This is valuable in automated
    /// testing scenarios to make application activation and teardown faster and to bypass
    /// potential problems with leadership election when the application is being frequently
    /// shut down from a debugger without closing cleanly. 
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection UseWolverineSoloMode(this IServiceCollection services)
    {
        services.AddSingleton<IWolverineExtension, UseSoloDurabilityMode>();
        return services;
    }

    /// <summary>
    /// Apply either overrides or additional configuration to Wolverine in this application
    /// Useful for testing overrides or for splitting configuration between modules
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IServiceCollection ConfigureWolverine(this IServiceCollection services,
        Action<WolverineOptions> configure)
    {
        var extension = new LambdaWolverineExtension(configure);
        services.AddSingleton<IWolverineExtension>(extension);

        return services;
    }

    internal class UseSoloDurabilityMode : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            options.Durability.Mode = DurabilityMode.Solo;
        }
    }
}
