using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

public class Bug_756_composite_handler_on_saga
{
    [Fact]
    public async Task compile_successfully()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<PcSagaExample>();
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_756";
                }).IntegrateWithWolverine();
            }).StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();

        await host.InvokeMessageAndWaitAsync(new PcDoSomething(Guid.NewGuid()));
    }
}

public record PcDoSomething(Guid Id);
public record PcDoSomethingElse(Guid Id);

[WolverineIgnore]
public class PcSagaExample : Wolverine.Saga
{
    public Guid Id { get; set; }

    public static (PcSagaExample, PcDoSomethingElse) Start(PcDoSomething message)
    {
        return (new PcSagaExample
        {
            Id = message.Id,
        }, new PcDoSomethingElse(message.Id));
    }

    [WolverineBefore]
    public PcExternalState? LookupExternalState(PcDoSomethingElse message)
    {
        return new PcExternalState();
    }

    public void Handle(PcDoSomethingElse message, PcExternalState? state)
    {
    }
}

public class PcExternalState
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
