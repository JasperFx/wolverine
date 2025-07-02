using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.Tests.DynamicCodeGen;

public class SharedDynamicAppFixture : IAsyncLifetime
{
    private readonly string _schemaName = "sch" + Guid.NewGuid().ToString().Replace("-", string.Empty);
    
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.CritterStackDefaults(x =>
        {
            x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
            x.ApplicationAssembly = GetType().Assembly;
        });
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
            opts.DatabaseSchemaName = _schemaName;
            opts.Events.DatabaseSchemaName = _schemaName;
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();
        
        Host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.WarmUpRoutes = RouteWarmup.Eager;
            });
        });
    }

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
    }
}

[CollectionDefinition(nameof(SharedDynamicAppFixture))]
public class ScenariosCollection : ICollectionFixture<SharedDynamicAppFixture>;

[Collection(nameof(SharedDynamicAppFixture))]
public class Bug_1546_dynamic_scheme_dynamic_code_gen(SharedDynamicAppFixture fixture)
{
    [Fact]
    public async Task One_way()
    {
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<DynamicSchemeAggregate>(id, new DynamicSchemeCreated());
        await session.SaveChangesAsync();
        await fixture.Host.ExecuteAndWaitAsync(async () => await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new OneWayRequest(id)).ToUrl("/dynamic-scheme/create-or-update/one-way");
            x.StatusCodeShouldBeSuccess();
        }));
        var aggregate = await session.Events.AggregateStreamAsync<DynamicSchemeAggregate>(id);
        Assert.NotNull(aggregate);
        Assert.Equal("updated", aggregate.Status);
    }

    [Fact]
    public async Task Or_the_other_way() =>
        await fixture.Host.ExecuteAndWaitAsync(async () => await fixture.Host.Scenario(x =>
        {
            x.Post.Url("/dynamic-scheme/create-or-update/other-way");
            x.StatusCodeShouldBeSuccess();
        }));
}


[Collection(nameof(SharedDynamicAppFixture))]
public class Bug_1546_dynamic_scheme_dynamic_code_gen_second_parellel_running_test(SharedDynamicAppFixture appFixture)
{
    [Fact]
    public void assert_configuration_is_valid()
    {
        appFixture.Host.AssertWolverineConfigurationIsValid();
    }
}


public record OneWayRequest(Guid Id);
public static class OneWayDynamicSchemeEndpoint
{
    [WolverinePost("dynamic-scheme/create-or-update/one-way")]
    public static async Task<IResult> Handle(IMessageBus bus, OneWayRequest request) => Results.Ok(await bus.InvokeAsync<DynamicSchemeAggregate>(new CreateDynamicSchemeOneWay(request.Id)));
}

public static class OtherWayDynamicSchemeEndpoint
{
    [WolverinePost("dynamic-scheme/create-or-update/other-way")]
    public static async Task<IResult> Handle(IMessageBus bus) => Results.Ok(await bus.InvokeAsync<DynamicSchemeAggregate>(new CreateDynamicSchemeOtherWay(Guid.NewGuid())));
}

public record DynamicSchemeCreated;
public record DynamicSchemeUpdated;

public class DynamicSchemeAggregate
{
    public Guid Id { get; set; }
    public required string Status { get; set; }

    public static DynamicSchemeAggregate Create(DynamicSchemeCreated _) => new()
    {
        Status = "created"
    };
    
    public static void Apply(DynamicSchemeUpdated _, DynamicSchemeAggregate aggregate)
    {
        aggregate.Status = "updated";
    }
}

public record CreateDynamicSchemeOneWay(Guid Id);

public static class CreateDynamicSchemeOneWayAggregateHandler
{
    
    public static (Events, UpdatedAggregate) Handle(CreateDynamicSchemeOneWay command, DynamicSchemeAggregate? aggregate)
    {
        return ([aggregate is { } ? new DynamicSchemeUpdated() : new DynamicSchemeCreated()], new());
    }
}

public record CreateDynamicSchemeOtherWay(Guid Id);

public static class CreateDynamicSchemeOtherWayAggregateHandler
{
    public static (Events, UpdatedAggregate) Handle(CreateDynamicSchemeOtherWay command, DynamicSchemeAggregate? aggregate)
    {
        return ([aggregate is { } ? new DynamicSchemeUpdated() : new DynamicSchemeCreated()], new());
    }
}