using System.Diagnostics;
using System.Reflection;
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
using JasperFx.RuntimeCompiler;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

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

        services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();

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

#if DEBUG
            if (directory.EndsWith("Debug", StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.ParentDirectory()!.ParentDirectory();
            }
            else if (directory.ParentDirectory()!.EndsWith("Debug", StringComparison.OrdinalIgnoreCase))
            {
                directory = directory.ParentDirectory()!.ParentDirectory()!.ParentDirectory();
            }
#endif

            // Don't correct for the path if it's already been set
            if (options.CodeGeneration.GeneratedCodeOutputPath == "Internal/Generated")
            {
                options.CodeGeneration.GeneratedCodeOutputPath =
                    directory!.AppendPath("Internal", "Generated");
            }

            return options;
        });

        services.AddSingleton<IWolverineRuntime, WolverineRuntime>();

        services.AddSingleton<ISystemPart, WolverineSystemPart>();

        services.AddSingleton(options.HandlerGraph);
        services.AddSingleton(options.Durability);
        
        // The runtime is also a hosted service
        services.AddSingleton(s => (IHostedService)s.GetRequiredService<IWolverineRuntime>());

        services.MessagingRootService(x => x.MessageTracking);

        services.TryAddSingleton<IMessageStore, NullMessageStore>();
        services.AddSingleton<InMemorySagaPersistor>();

        services.MessagingRootService(x => x.Pipeline);

        services.AddOptions();
        services.AddLogging();

        services.AddScoped<IMessageBus, MessageContext>();
        services.AddScoped<IMessageContext, MessageContext>();

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
            ExtensionLoader.ApplyExtensions(options);
        }

        if (options.ApplicationAssembly != null)
        {
            options.HandlerGraph.Discovery.Assemblies.Fill(options.ApplicationAssembly);
        }

        configure?.Invoke(options);

        options.ApplyLazyConfiguration();

        return services;
    }

#if NET8_0_OR_GREATER
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
    #else
    /// <summary>
    /// Bootstrap Wolverine into a HostApplicationBuilder
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static HostApplicationBuilder UseWolverine(this HostApplicationBuilder builder,
        Action<WolverineOptions>? configure)
    {
        builder.Services.AddWolverine(configure);

        return builder;
    }
#endif

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
    public static IServiceCollection AddWolverineExtension<T>(this IServiceCollection services) where T : class, IWolverineExtension
    {
        return services.AddSingleton<IWolverineExtension, T>();
    }

    /// <summary>
    /// Add an asynchronous wolverine extension to the IoC container to apply extra configuration to your system
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddAsyncWolverineExtension<T>(this IServiceCollection services) where T : class, IAsyncWolverineExtension
    {
        return services.AddSingleton<IAsyncWolverineExtension, T>();
    }

    public static void AssertWolverineConfigurationIsValid(this IHost host)
    {
        host.AssertAllGeneratedCodeCanCompile();
    }
    
    /// <summary>
    ///     Validate all of the Wolverine configuration of this Wolverine application.
    ///     This checks that all of the known generated code elements are valid
    /// </summary>
    /// <param name="host"></param>
    // TODO -- put this back into JasperFx.RuntimeCompiler!
    public static void AssertAllGeneratedCodeCanCompile(this IHost host)
    {
        var exceptions = new List<Exception>();
        var failures = new List<string>();
        
        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();

        var services = host.Services.GetService<IServiceVariableSource>();

        foreach (var collection in collections)
        {
            foreach (var file in collection.BuildFiles())
            {
                var fileName = collection.ChildNamespace.Replace(".", "/").AppendPath(file.FileName);
                
                try
                {
                    var assembly = new GeneratedAssembly(collection.Rules);
                    file.AssembleTypes(assembly);
                    new AssemblyGenerator().Compile(assembly, services);
                    
                    Debug.WriteLine($"U+2713 {fileName} ");
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"Failed: {fileName}");
                    Debug.WriteLine(e);
                    
                    failures.Add(fileName);
                    exceptions.Add(e);
                }
            }
        }

        if (failures.Any())
        {
            throw new AggregateException($"Compilation failures for:\n{failures.Join("\n")}", exceptions);
        }
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
        services.AddSingleton<IMessageStore, NullMessageStore>();
        services.AddSingleton<IWolverineExtension, DisablePersistence>();
        return services;
    }

    #region sample_DisableExternalTransports

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

    internal class UseSoloDurabilityMode : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            options.Durability.Mode = DurabilityMode.Solo;
        }
    }
}