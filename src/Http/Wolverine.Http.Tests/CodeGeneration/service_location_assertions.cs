using System.Diagnostics;
using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.CodeGeneration;

// This also tests the message handler mechanics too
// It was just convenient to get it all in one place
public class service_location_assertions
{
    public readonly ServiceDescriptor descriptor1 = new ServiceDescriptor(typeof(IWidget), typeof(AWidget));
    public readonly ServiceDescriptor descripter2 = new ServiceDescriptor(typeof(IWidget), "B", typeof(BWidget));
    public readonly HttpChain theChain = new HttpChain(MethodCall.For<WidgetEndpoint>(x  => x.Post(null, null, null, null)), new HttpGraph(new WolverineOptions(), Substitute.For<IServiceContainer>()));
    public readonly RecordingLogger theLogger = new();
    
    private IServiceProvider servicesWithPolicy(ServiceLocationPolicy policy)
    {
        var services = new ServiceCollection();
        var options = new WolverineOptions { ServiceLocationPolicy = policy };
        services.AddSingleton(options);
        services.AddSingleton<ILoggerFactory>(theLogger);
        return services.BuildServiceProvider();
    }

    public interface IServiceGatewayUsingRefit;

    public static void configure_with_always_use_service_locator()
    {
        #region sample_always_use_service_location

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // other configuration

            // Use a service locator for this service w/o forcing the entire
            // message handler adapter to use a service locator for everything
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IServiceGatewayUsingRefit>();
        });

        #endregion
    }

    private async Task<IAlbaHost> buildHost(ServiceProviderSource providerSource, Action<WolverineOptions> configure)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);

            opts.Services.AddScoped<IThing>(s => new BigThing());
            
            opts.IncludeType(typeof(CSP5User));
            opts.CodeGeneration.AlwaysUseServiceLocationFor<IFlag>();

            opts.Services.AddScoped<IGateway, Gateway>();
            opts.Services.AddScoped<IFlag>(x =>
            {
                var context = x.GetRequiredService<ColorContext>();
                return context.Color.EqualsIgnoreCase("red") ? new RedFlag() : new GreenFlag();
            });

            opts.Services.AddSingleton(new ColorContext("Red"));
            
            configure(opts);
        });
        
        // config
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "myapp";
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.ServiceProviderSource = providerSource;
                opts.SourceServiceFromHttpContext<IThing>();
            });
        });
    }

    [Fact]
    public void do_not_throw_exception_if_allowing_service_locations_and_always_allowed()
    {
        var services = servicesWithPolicy(ServiceLocationPolicy.AlwaysAllowed);

        var reports = new[]
        {
            new ServiceLocationReport(descriptor1, "Because I said so!"),
            new ServiceLocationReport(descripter2, "I didn't like this one")
        };
        
        theChain.AssertServiceLocationsAreAllowed(reports, services);
        
        // Don't even bother to log anything if we're AlwaysAllowed
        theLogger.Messages.Count.ShouldBe(0);
    }
    
    [Fact]
    public void do_not_throw_exception_if_allowing_service_locations_and_allowed_but_warn()
    {
        var services = servicesWithPolicy(ServiceLocationPolicy.AllowedButWarn);

        var reports = new[]
        {
            new ServiceLocationReport(descriptor1, "Because I said so!"),
            new ServiceLocationReport(descripter2, "I didn't like this one")
        };
        
        theChain.AssertServiceLocationsAreAllowed(reports, services);
        
        // Don't even bother to log anything if we're AlwaysAllowed
        theLogger.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public void throw_exception_if_not_allowing_service_locations()
    {
        var services = servicesWithPolicy(ServiceLocationPolicy.NotAllowed);

        var reports = new[]
        {
            new ServiceLocationReport(descriptor1, "Because I said so!"),
            new ServiceLocationReport(descripter2, "I didn't like this one")
        };

        Should.Throw<InvalidServiceLocationException>(() =>
        {
            theChain.AssertServiceLocationsAreAllowed(reports, services);
        });
    }
    
    [Theory]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed)]
    [InlineData(ServiceLocationPolicy.NotAllowed)]
    [InlineData(ServiceLocationPolicy.AllowedButWarn)]
    public void do_not_throw_exception_if_allowing_service_locations_and_none(ServiceLocationPolicy policy)
    {
        var services = servicesWithPolicy(policy);
        theChain.AssertServiceLocationsAreAllowed([], services);
        
        // Don't even bother to log anything if we're AlwaysAllowed
        theLogger.Messages.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(ServiceLocationPolicy.AllowedButWarn, ServiceProviderSource.IsolatedAndScoped)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed, ServiceProviderSource.IsolatedAndScoped)]
    [InlineData(ServiceLocationPolicy.AllowedButWarn, ServiceProviderSource.FromHttpContextRequestServices)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed, ServiceProviderSource.FromHttpContextRequestServices)]
    public async Task can_use_service_locations_with_http(ServiceLocationPolicy policy, ServiceProviderSource source)
    {
        await using var host = await buildHost(source, x =>
        {
            x.ServiceLocationPolicy = policy;
            x.Services.AddScoped<IWidget>(_ => new AWidget());
        });

        await host.Scenario(x =>
        {
            x.Post.Json(new WidgetRequest()).ToUrl("/service/locations");
        });
    }

    [Fact]
    public async Task blow_up_and_deny_on_HTTP_when_not_allowing_service_location()
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped,x =>
        {
            x.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;
            x.Services.AddScoped<IWidget>(_ => new AWidget());
        });
        
        await host.Scenario(x =>
        {
            x.Post.Json(new WidgetRequest()).ToUrl("/service/locations");
            x.StatusCodeShouldBe(500);
        });
    }

    [Fact]
    public async Task block_and_deny_on_message_handlers_when_not_allowing_service_location()
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped,x =>
        {
            x.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;
            x.Services.AddScoped<IWidget>(_ => new AWidget());
        });
        
        await Should.ThrowAsync<InvalidServiceLocationException>(async () =>
        {
            await host.InvokeAsync(new UseWidget());
        });
    }
    
    [Theory]
    [InlineData(ServiceLocationPolicy.AllowedButWarn)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed)]
    public async Task can_use_service_locations_with_handler(ServiceLocationPolicy policy)
    {
        await using var host = await buildHost(ServiceProviderSource.IsolatedAndScoped,x =>
        {
            x.ServiceLocationPolicy = policy;
            x.Services.AddScoped<IWidget>(_ => new AWidget());
        });

        await host.InvokeAsync(new UseWidget());


    }

    [Theory]
    [InlineData(ServiceLocationPolicy.AllowedButWarn, ServiceProviderSource.IsolatedAndScoped)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed, ServiceProviderSource.IsolatedAndScoped)]
    [InlineData(ServiceLocationPolicy.AllowedButWarn, ServiceProviderSource.FromHttpContextRequestServices)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed, ServiceProviderSource.FromHttpContextRequestServices)]
    public async Task always_use_service_location_does_not_count_toward_validation(ServiceLocationPolicy policy, ServiceProviderSource source)
    {
        await using var host = await buildHost(source, opts =>
        {
            opts.ServiceLocationPolicy = policy;
        });

        CSP5User.Flag = null;
        
        await host.InvokeAsync(new CSP5());
        CSP5User.Flag.ShouldBeOfType<RedFlag>();
    }
    
    [Theory]
    [InlineData(ServiceLocationPolicy.AllowedButWarn, ServiceProviderSource.IsolatedAndScoped)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed, ServiceProviderSource.IsolatedAndScoped)]
    [InlineData(ServiceLocationPolicy.AllowedButWarn, ServiceProviderSource.FromHttpContextRequestServices)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed, ServiceProviderSource.FromHttpContextRequestServices)]
    public async Task always_use_service_location_does_not_count_toward_validation_in_http_endpoint(ServiceLocationPolicy policy, ServiceProviderSource source)
    {
        await using var host = await buildHost(source, opts =>
        {
            opts.ServiceLocationPolicy = policy;
        });

        CSP5User.Flag = null;

        await host.Scenario(x =>
        {
            x.Post.Json(new CSP5()).ToUrl("/csp5");
            x.StatusCodeShouldBe(204);
        });
        
        CSP5User.Flag.ShouldBeOfType<RedFlag>();
    }
}

public interface IWidget;
public class AWidget : IWidget;

public class BWidget : IWidget;

public record WidgetRequest;
public class WidgetEndpoint
{
    [WolverinePost("/service/locations")]
    public string Post(WidgetRequest request, IWidget widget, IThing thing, HttpContext context)
    {
        thing.ShouldNotBeNull();
        
        context.RequestServices.GetRequiredService<IThing>().ShouldBeSameAs(thing);
        
        return widget.ToString();
    }
}

public interface IThing;

public class BigThing : IThing;

public record UseWidget;

public static class UseWidgetHandler
{
    public static void Handle(UseWidget command, IWidget service) => Debug.WriteLine("Got me a widget to use");
}

public class RecordingLogger : ILoggerFactory, ILogger
{
    public List<string> Messages { get; } = new();
    
    public void Dispose()
    {
        
    }

    public void AddProvider(ILoggerProvider provider)
    {

    }

    public ILogger CreateLogger(string categoryName)
    {
        return this;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return this;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }
}

public record CSP5;

public static class CSP5User
{
    public static IFlag? Flag { get; set; } 
    
    [WolverinePost("/csp5")]
    public static void Handle(CSP5 message, IFlag flag, IGateway gateway)
    {
        Flag = flag;
    }
}



public interface IFlag;
public record ColorContext(string Color);

public record RedFlag : IFlag;
public record GreenFlag : IFlag;

public interface IGateway;
public class Gateway : IGateway;

public interface IUserContext
{
    public string UserId { get;}
}

public class UserContext : IUserContext
{
    public string UserId { get; set; }
}

public class UserContextFactory
{
    public IUserContext Build(HttpContext context) => new UserContext();
}

public class MyCustomUserMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;
    
    public async Task InvokeAsync(HttpContext httpContext)
    {
        // and whatever else
        await _next(httpContext);
    }
}

public static class SampleServiceLocation
{
    public static async Task<int> bootstrap(string[] args)
    {
        #region sample_bootstrapping_with_httpcontext_request_services

        var builder = WebApplication.CreateBuilder();

        builder.UseWolverine(opts =>
        {
            // more configuration
        });

        // Just pretend that this IUserContext is being 
        builder.Services.AddScoped<IUserContext, UserContext>();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();

        // Custom middleware that is somehow configuring our IUserContext
        // that might be getting used within 
        app.UseMiddleware<MyCustomUserMiddleware>();
        
        app.MapWolverineEndpoints(opts =>
        {
            // Opt into using the shared HttpContext.RequestServices scoped
            // container any time Wolverine has to use a service locator
            opts.ServiceProviderSource = ServiceProviderSource.FromHttpContextRequestServices;
            
            // OR this is the default behavior to be backwards compatible:
            opts.ServiceProviderSource = ServiceProviderSource.IsolatedAndScoped;
            
            // We're telling Wolverine that the IUserContext should always
            // be pulled from HttpContext.RequestServices
            // and this happens regardless of the ServerProviderSource!
            opts.SourceServiceFromHttpContext<IUserContext>();
        });

        return await app.RunJasperFxCommands(args);

        #endregion
    }
}