using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Bugs;

public class Bug_2387_write_aggregate_throw_exception_codegen : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DisableNpgsqlLogging = true;
                        m.Events.UseIdentityMapForAggregates = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task codegen_compiles_with_write_aggregate_throw_exception_and_validate()
    {
        // This test reproduces Bug #2387:
        // When a message handler has [WriteAggregate(OnMissing = ThrowException)]
        // AND a Validate() method that takes the aggregate as a parameter,
        // AND AutoApplyTransactions + UseIdentityMapForAggregates,
        // the codegen generates code where stream_entity is referenced before
        // it is declared (the null check and Validate both use stream_entity.Aggregate
        // before the batch query assigns stream_entity).

        await using var session = _store.LightweightSession();
        var action = session.Events.StartStream<Bug2387Aggregate>(new Bug2387Created("test"));
        await session.SaveChangesAsync();

        // If codegen is broken, this will throw a compilation error:
        // CS0841: Cannot use local variable 'stream_entity' before it is declared
        await _host.InvokeMessageAndWaitAsync(new Bug2387DeleteCommand(action.Id));
    }

    [Fact]
    public async Task throws_on_missing_aggregate_with_validate()
    {
        var nonExistentId = Guid.NewGuid();

        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeMessageAndWaitAsync(new Bug2387DeleteCommand(nonExistentId));
        });
    }
}

public record Bug2387Created(string Name);
public record Bug2387Deleted(Guid Id);

public record Bug2387DeleteCommand(Guid Id);

public class Bug2387Aggregate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }

    public void Apply(Bug2387Created created) => Name = created.Name;
    public void Apply(Bug2387Deleted deleted) => IsDeleted = true;
}

// Matches the exact handler structure from the user's reproduction repo
public class Bug2387DeleteHandler
{
    public static async Task BeforeAsync()
    {
        Console.WriteLine("Some Before tasks");
    }

    public static void Validate(Bug2387Aggregate entity)
    {
        Console.WriteLine("Some validation checks");
    }

    public static IEvent Handle(
        Bug2387DeleteCommand command,
        [WriteAggregate(Required = true, OnMissing = OnMissing.ThrowException)]
        Bug2387Aggregate entity)
    {
        return Event.For(new Bug2387Deleted(entity.Id));
    }
}
