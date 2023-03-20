using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Squadron;
using TestingSupport.Compliance;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Transports.Postgresql;

public class SendAndReceiveTests : IClassFixture<ExtendedPostgresResource>
{
    private readonly ExtendedPostgresResource _resource;

    public SendAndReceiveTests(ExtendedPostgresResource resource)
    {
        _resource = resource;
    }

    [Fact(Skip = "Just used for tests at the moment")]
    public async Task Should_SendAndReceiveMessage()
    {
        var connectionString = await _resource.GetConnectionStringAsync();
        await CreateListeners(connectionString);
        var bus = await CreateMessageBus(connectionString);

        await Task.Delay(10);
        while (true)
        {
            await bus.PublishAsync(new PingMessage());
            //await bus.BroadcastToTopicAsync("test", new Ponged());
            // var foo = await bus.InvokeAsync<PongMessage>(
            //     new PingMessage { Number = 1 },
            //     default,
            //     TimeSpan.FromDays(5));

            await Task.Delay(10000000);
        }
    }

    private async Task<IMessageBus> CreateMessageBus(string connectionString)
    {
        var host = new HostBuilder()
            .UseWolverine(options =>
            {
                options.UseSystemTextJsonForSerialization();
                options.Discovery
                    .DisableConventionalDiscovery()
                    .IncludeType<PongHandler>()
                    .IncludeType<PingMessage>();
                options.UsePostgres(connectionString)
                    .UseConventionalRouting()
                    .AutoProvision();
                options.ListenForMessagesFrom(new Uri("postgresql://queue/replies"))
                    .UseForReplies();
            })
            .Build();
        var runtime = (IHostedService) host.Services.GetRequiredService<IWolverineRuntime>();
        await runtime.StartAsync(default);
        var bus = host.Services.GetRequiredService<IMessageBus>();
        return bus;
    }

    private async Task CreateListeners(string connectionString)
    {
        var host = new HostBuilder()
            .UseWolverine(options =>
            {
                options.UseSystemTextJsonForSerialization();
                options.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PingHandler>()
                    .IncludeType<PongMessage>();
                options.UsePostgres(connectionString).UseConventionalRouting().AutoProvision();
            })
            .Build();
        var runtime = (IHostedService) host.Services.GetRequiredService<IWolverineRuntime>();
        await runtime.StartAsync(default);
    }
}

public class ExtendedPostgresResourceOptions : PostgreSqlDefaultOptions
{
    public override void Configure(ContainerResourceBuilder builder)
    {
        base.Configure(builder);
        //builder.Password("42248704af85c25f818a");
        //builder.ExternalPort(59093);
    }
}

public class ExtendedPostgresResource : PostgreSqlResource<ExtendedPostgresResourceOptions>
{
    public async Task<string> GetConnectionStringAsync()
    {
        var dbName = $"W_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(dbName);
        return GetConnectionString(dbName);
    }
}

public static class C
{
    public static int Counter = 0;
}

public class PingHandler
{
    // Simple message handler for the PingMessage message type
    public PongMessage Handle(
        // The first argument is assumed to be the message type
        PingMessage message,

        // Wolverine supports method injection similar to ASP.Net Core MVC
        // In this case though, IMessageContext is scoped to the message
        // being handled
        IMessageContext context)
    {
        var response = new PongMessage
        {
            Number = message.Number + 1
        };

        Interlocked.Increment(ref C.Counter);

        return response;
    }
}

public class PongHandler
{
    // Simple message handler for the PingMessage message type
    public PingMessage Handle(
        // The first argument is assumed to be the message type
        PongMessage message,

        // Wolverine supports method injection similar to ASP.Net Core MVC
        // In this case though, IMessageContext is scoped to the message
        // being handled
        IMessageContext context)
    {
        var response = new PingMessage
        {
            Number = message.Number + 1
        };

        if (response.Number > 1000)
        {
            return default;
        }

        return response;
    }
}

public class PingMessage
{
    public int Number { get; set; }
}

public class PongMessage
{
    public int Number { get; set; }
}

public class Ponged
{
    public int Number { get; set; }
}
