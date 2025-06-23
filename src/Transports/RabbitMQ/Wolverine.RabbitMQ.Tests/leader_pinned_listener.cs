using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class leader_pinned_listener : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private const string? LeaderPinnedListeners = "leader_pinned_listeners";
    private List<IHost> _hosts = new List<IHost>();

    public leader_pinned_listener(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<IHost> startHost()
    {
        await dropSchemaAsync();
        
        var host =  await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This is where I'm adding in the custom ILoggerProvider
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
                
                opts.UseRabbitMq().DisableDeadLetterQueueing().EnableWolverineControlQueues().AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, LeaderPinnedListeners);

                opts.ListenToRabbitQueue("admin1").ListenOnlyAtLeader();
                opts.ListenToRabbitQueue("admin2").ListenOnlyAtLeader();
            }).StartAsync();
        
        _hosts.Add(host);
        
        new XUnitEventObserver(host, _output);

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
        }
    }
    
    private static async Task dropSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(LeaderPinnedListeners);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task the_leader_pinned_listeners_only_run_on_the_leader()
    {
        var host = await startHost();
        await host.WaitUntilAssumesLeadershipAsync(30.Seconds());

        await host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = LeaderPinnedListenerFamily.SchemeName;
            w.ExpectRunningAgents(host, 2);
        }, 30.Seconds());

        var listeners = host.GetRuntime().Endpoints.ActiveListeners().Where(x => x.Endpoint.Role == EndpointRole.Application).Select(x => x.Uri).ToArray();
        listeners.Length.ShouldBe(2);
        listeners.ShouldContain(new Uri("rabbitmq://queue/admin1"));
        listeners.ShouldContain(new Uri("rabbitmq://queue/admin2"));
        
        // Spin up a couple more nodes
        var host2 = await startHost();
        var host3 = await startHost();
        var host4 = await startHost();

        // No change
        await host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = LeaderPinnedListenerFamily.SchemeName;
            w.ExpectRunningAgents(host, 2);
            w.ExpectRunningAgents(host2, 0);
            w.ExpectRunningAgents(host3, 0);
            w.ExpectRunningAgents(host4, 0);
        }, 5.Seconds());
        
        host2.GetRuntime().Endpoints.ActiveListeners().Where(x => x.Endpoint.Role == EndpointRole.Application).Any(x => x.Uri.Scheme == "rabbitmq").ShouldBeFalse();
        host3.GetRuntime().Endpoints.ActiveListeners().Where(x => x.Endpoint.Role == EndpointRole.Application).Any(x => x.Uri.Scheme == "rabbitmq").ShouldBeFalse();
        host4.GetRuntime().Endpoints.ActiveListeners().Where(x => x.Endpoint.Role == EndpointRole.Application).Any(x => x.Uri.Scheme == "rabbitmq").ShouldBeFalse();

        await host.StopAsync();
        _hosts.Remove(host);
        
        await host2.WaitUntilAssumesLeadershipAsync(30.Seconds());
        await host2.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = LeaderPinnedListenerFamily.SchemeName;
            w.ExpectRunningAgents(host2, 2);
            w.ExpectRunningAgents(host3, 0);
            w.ExpectRunningAgents(host4, 0);
        }, 5.Seconds());
    }
}

public class XUnitLogger : ILogger
{
    private readonly string _categoryName;

    private readonly List<string> _ignoredStrings = new()
    {
        "Declared",
        "Successfully processed message"
    };

    private readonly ITestOutputHelper _testOutputHelper;

    public XUnitLogger(ITestOutputHelper testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return new Disposable();
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (exception is DivideByZeroException)
        {
            return;
        }

        if (exception is BadImageFormatException)
        {
            return;
        }

        // Obviously this is crude and you would do something different here...
        if (_categoryName == "Wolverine.Transports.Sending.BufferedSendingAgent" &&
            logLevel == LogLevel.Information) return;
        if (_categoryName == "Wolverine.Runtime.WolverineRuntime" &&
            logLevel == LogLevel.Information) return;
        if (_categoryName == "Microsoft.Hosting.Lifetime" &&
            logLevel == LogLevel.Information) return;
        if (_categoryName == "Wolverine.Transports.ListeningAgent" &&
            logLevel == LogLevel.Information) return;
        if (_categoryName == "JasperFx.Resources.ResourceSetupHostService" &&
            logLevel == LogLevel.Information) return;
        if (_categoryName == "Wolverine.Configuration.HandlerDiscovery" &&
            logLevel == LogLevel.Information) return;
        
        var text = formatter(state, exception);
        if (_ignoredStrings.Any(x => text.Contains(x))) return;

        _testOutputHelper.WriteLine($"{_categoryName}/{logLevel}: {text}");

        if (exception != null)
        {
            _testOutputHelper.WriteLine(exception.ToString());
        }
    }

    public class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public class OutputLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public OutputLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }


    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(_output, categoryName);
    }
}