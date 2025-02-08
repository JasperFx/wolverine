using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_310_saga_handler_that_returns_another_saga : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<Saga1>().IncludeType<Saga2>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task one_saga_spawns_another()
    {
        var id = Guid.NewGuid();
        var tracked = await _host.InvokeMessageAndWaitAsync(new StartSaga1(id));

        // Should *not* be sending any messages. Should be handling the Saga2 return value as a new
        // Saga
        tracked.Sent.MessagesOf<Saga2>().Any().ShouldBeFalse();

        using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        var saga1 = await session.LoadAsync<Saga1>(id);
        saga1.One.ShouldBeTrue();

        var saga2 = await session.LoadAsync<Saga2>(id);
        saga2.Two.ShouldBeTrue();
        saga2.Three.ShouldBeTrue();
    }
}

public record StartSaga1(Guid Id);

[WolverineIgnore]
public class Saga1 : Wolverine.Saga
{
    public Guid Id { get; set; }

    public bool One { get; set; }
    public bool Two { get; set; }
    public bool Three { get; set; }

    public static (Saga1, S1) Start(StartSaga1 command)
    {
        return (new Saga1 { Id = command.Id }, new S1(command.Id));
    }

    public (Saga2, S2) Handle(S1 command)
    {
        One = true;
        return (new Saga2 { Id = Id }, new S2(Id));
    }
}

[WolverineIgnore]
public class Saga2 : Wolverine.Saga
{
    public Guid Id { get; set; }

    public bool One { get; set; }
    public bool Two { get; set; }
    public bool Three { get; set; }

    public S3 Handle(S2 command)
    {
        Two = true;
        return new S3(Id);
    }

    public void Handle(S3 command)
    {
        Three = true;
    }
}

public record S1(Guid Id);
public record S2(Guid Id);
public record S3(Guid Id);