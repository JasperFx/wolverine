using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

public class Bug_310_saga_handler_that_returns_another_saga : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_310";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<PcSagaA>().IncludeType<PcSagaB>();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
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
        var tracked = await _host.InvokeMessageAndWaitAsync(new StartPcSagaA(id));

        // Should *not* be sending any messages. Should be handling the PcSagaB return value as a new Saga
        tracked.Sent.MessagesOf<PcSagaB>().Any().ShouldBeFalse();

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        var sagaA = await session.LoadAsync<PcSagaA>(id);
        sagaA.One.ShouldBeTrue();

        var sagaB = await session.LoadAsync<PcSagaB>(id);
        sagaB.Two.ShouldBeTrue();
        sagaB.Three.ShouldBeTrue();
    }
}

public record StartPcSagaA(Guid Id);

[WolverineIgnore]
public class PcSagaA : Wolverine.Saga
{
    public Guid Id { get; set; }

    public bool One { get; set; }
    public bool Two { get; set; }
    public bool Three { get; set; }

    public static (PcSagaA, PcSA1) Start(StartPcSagaA command)
    {
        return (new PcSagaA { Id = command.Id }, new PcSA1(command.Id));
    }

    public (PcSagaB, PcSA2) Handle(PcSA1 command)
    {
        One = true;
        return (new PcSagaB { Id = Id }, new PcSA2(Id));
    }
}

[WolverineIgnore]
public class PcSagaB : Wolverine.Saga
{
    public Guid Id { get; set; }

    public bool One { get; set; }
    public bool Two { get; set; }
    public bool Three { get; set; }

    public PcSA3 Handle(PcSA2 command)
    {
        Two = true;
        return new PcSA3(Id);
    }

    public void Handle(PcSA3 command)
    {
        Three = true;
    }
}

public record PcSA1(Guid Id);
public record PcSA2(Guid Id);
public record PcSA3(Guid Id);
