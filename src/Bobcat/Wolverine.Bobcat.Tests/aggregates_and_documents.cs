using IntegrationTests;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Fisher;
using Marten;
using Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.AggregatesAndDocuments;

// GH-4921: an aggregate built from events is checked through FetchLatest (ThenAggregate), a stored
// document through LoadAsync (ThenDocument), each against any of the three expected-object forms
// (bobcat#450) -- on Marten, Polecat and Fisher.

public record AdmitDog(Guid Id, string Name, int Age);
public record DogAdmitted(string Name, int Age);
public record RegisterKennel(Guid Id, string Name, int Capacity, string Wing);

public class Dog
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }

    public void Apply(DogAdmitted e)
    {
        Name = e.Name;
        Age = e.Age;
    }
}

public class Kennel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public string Wing { get; set; } = "";
}

public static class AdmitDogHandler
{
    public static StartStream Handle(AdmitDog command) => Storage.StartStream<Dog>(command.Id, new DogAdmitted(command.Name, command.Age));
}

public static class RegisterKennelHandler
{
    public static IStorageAction<Kennel> Handle(RegisterKennel command)
        => Storage.Insert(new Kennel { Id = command.Id, Name = command.Name, Capacity = command.Capacity, Wing = command.Wing });
}

public abstract class ShelterHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AdmitDogHandler))
                    .IncludeType(typeof(RegisterKennelHandler));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenShelterHost : ShelterHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "bobcat_aggregates_and_documents";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
}

public sealed class PolecatShelterHost : ShelterHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "bobcat_aggregates_and_documents";
        }).IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class FisherShelterHost : ShelterHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_aggregates_and_documents_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class aggregates_and_documents<THost>(THost app) : WolverineSpec(app.Host)
    where THost : ShelterHost
{
    [Fact]
    public async Task an_aggregate_is_checked_through_fetch_latest_with_assertions_on_its_members()
    {
        var theDog = Guid.CreateVersion7();

        await WhenReceived(new AdmitDog(theDog, "Rex", 4));

        // Wolverine builds with C# 12, where an expression tree must spell out every optional argument
        // (Shouldly's customMessage); from C# 14 -- the net10.0 default -- x.Age.ShouldBeGreaterThan(3) is enough
        var dog = await ThenAggregate<Dog>(theDog, Specify<Dog>(x => x.Age.ShouldBeGreaterThan(3, null), x => x.Age.ShouldBeLessThan(10, null)));
        dog.Age.ShouldBe(4);

        await ThenAggregate<Dog>(theDog, Specify<Dog>().With(x => x.Name, "Rex"));
    }

    [Fact]
    public async Task an_aggregate_that_does_not_match_fails()
    {
        var theDog = Guid.CreateVersion7();

        await WhenReceived(new AdmitDog(theDog, "Rex", 4));

        await Should.ThrowAsync<Exception>(() => ThenAggregate<Dog>(theDog, Specify<Dog>(x => x.Age.ShouldBeGreaterThan(10, null))));
    }

    [Fact]
    public async Task no_stream_means_no_aggregate()
    {
        var failure = await Should.ThrowAsync<Exception>(() => ThenAggregate<Dog>(Guid.CreateVersion7()));
        failure.Message.ShouldContain("FetchLatest found no stream");
    }

    [Fact]
    public async Task a_document_is_checked_through_load_async_against_a_table()
    {
        var theKennel = Guid.CreateVersion7();

        await WhenReceived(new RegisterKennel(theKennel, "North Run", 12, "East"));

        await ThenDocument<Kennel>(theKennel, Specify<Kennel>($$"""
            | Property | Value          |
            | Id       | {{theKennel}}  |
            | Name     | North Run      |
            | Capacity | 12             |
            | Wing     | East           |
            """));
    }
}

public sealed class marten_aggregates_and_documents(MartenShelterHost app)
    : aggregates_and_documents<MartenShelterHost>(app), IClassFixture<MartenShelterHost>;

public sealed class polecat_aggregates_and_documents(PolecatShelterHost app)
    : aggregates_and_documents<PolecatShelterHost>(app), IClassFixture<PolecatShelterHost>;

public sealed class fisher_aggregates_and_documents(FisherShelterHost app)
    : aggregates_and_documents<FisherShelterHost>(app), IClassFixture<FisherShelterHost>;
