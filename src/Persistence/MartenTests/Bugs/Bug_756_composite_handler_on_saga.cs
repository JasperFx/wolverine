using IntegrationTests;
using Marten;
using Marten.Events.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_756_composite_handler_on_saga
{
    [Fact]
    public async Task compile_successfully()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType<SagaExample>();
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();

            }).StartAsync();

        await host.InvokeMessageAndWaitAsync(new DoSomething(Guid.NewGuid()));
    }
}

public record DoSomething(Guid Id);
public record DoSomethingElse(Guid Id);

[WolverineIgnore]
public class SagaExample : Wolverine.Saga
{
    public Guid Id { get; set; }

    public static (SagaExample, DoSomethingElse) Start(DoSomething message)
    {
        return (new SagaExample
        {
            Id = message.Id,
        }, new DoSomethingElse(message.Id));
    }

    [WolverineBefore]
    public ExternalState? LookupExternalState(DoSomethingElse message)
    {
        return new ExternalState();
    }

    public void Handle(DoSomethingElse message, ExternalState? state)
    {
    }
}

public class ExternalState
{
    public Guid Id { get; set; } = Guid.NewGuid();
}