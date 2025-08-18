using System.Diagnostics;
using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.CodeGeneration;

// This also tests the message handler mechanics too
// It was just convenient to get it all in one place
public class service_location_assertions
{
    public readonly ServiceDescriptor descriptor1 = new ServiceDescriptor(typeof(IWidget), typeof(AWidget));
    public readonly ServiceDescriptor descripter2 = new ServiceDescriptor(typeof(IWidget), "B", typeof(BWidget));
    public readonly HttpChain theChain = new HttpChain(MethodCall.For<WidgetEndpoint>(x  => x.Post(null, null)), new HttpGraph(new WolverineOptions(), Substitute.For<IServiceContainer>()));
    public readonly RecordingLogger theLogger = new();
    
    private IServiceProvider servicesWithPolicy(ServiceLocationPolicy policy)
    {
        var services = new ServiceCollection();
        var options = new WolverineOptions { ServiceLocationPolicy = policy };
        services.AddSingleton(options);
        services.AddSingleton<ILoggerFactory>(theLogger);
        return services.BuildServiceProvider();
    }

    private async Task<IAlbaHost> buildHost(Action<WolverineOptions> configure)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
            configure(opts);
        });
        
        // config
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "myapp";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {

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
    [InlineData(ServiceLocationPolicy.AllowedButWarn)]
    [InlineData(ServiceLocationPolicy.AlwaysAllowed)]
    public async Task can_use_service_locations_with_http(ServiceLocationPolicy policy)
    {
        await using var host = await buildHost(x =>
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
        await using var host = await buildHost(x =>
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
        await using var host = await buildHost(x =>
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
        await using var host = await buildHost(x =>
        {
            x.ServiceLocationPolicy = policy;
            x.Services.AddScoped<IWidget>(_ => new AWidget());
        });

        await host.InvokeAsync(new UseWidget());


    }
}

public interface IWidget;
public class AWidget : IWidget;

public class BWidget : IWidget;

public record WidgetRequest;
public class WidgetEndpoint
{
    [WolverinePost("/service/locations")]
    public string Post(WidgetRequest request, IWidget widget)
    {
        return widget.ToString();
    }
}

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