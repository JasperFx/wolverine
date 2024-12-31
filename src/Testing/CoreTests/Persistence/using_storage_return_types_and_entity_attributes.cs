using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence;

public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        // Nothing, just use the in memory persistor
    }

    public override Task<Todo?> Load(string id)
    {
        return Task.FromResult(Host.Services.GetRequiredService<InMemorySagaPersistor>().Load<Todo>(id));
    }

    public override Task Persist(Todo todo)
    {
        Host.Services.GetRequiredService<InMemorySagaPersistor>().Store(todo);
        return Task.CompletedTask;
    }
}

public class using_multiple_storage_actions : IntegrationContext
{
    public using_multiple_storage_actions(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task use_multiple_storage_actions_of_different_types()
    {
        var command = new CreateTeamAndPlayer(Guid.NewGuid(), "Chiefs", "Patrick Mahomes");
        await Host.InvokeMessageAndWaitAsync(command);

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();
        persistor.Load<Team>(command.Id).Name.ShouldBe(command.TeamName);
        persistor.Load<Player>(command.Id).Name.ShouldBe(command.PlayerName);
    }

    [Fact]
    public async Task use_tuple_of_multiple_actions_of_same_entity()
    {   
        var command = new CreateMultiplePositions("Shortstop", "Pitcher");
        await Host.InvokeMessageAndWaitAsync(command);

        var persistor = Host.Services.GetRequiredService<InMemorySagaPersistor>();
        persistor.Load<Position>("Shortstop").ShouldNotBeNull();
        persistor.Load<Position>("Pitcher").ShouldNotBeNull();
    }
}

public class Team
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class Player
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public record CreateTeamAndPlayer(Guid Id, string TeamName, string PlayerName);

public record CreateMultiplePositions(string First, string Second);

public class Position
{
    public string Id { get; set; }
}

public static class CreateMultiplePositionsHandler
{
    public static (IStorageAction<Position>, IStorageAction<Position>) Handle(CreateMultiplePositions command)
    {
        return (Storage.Insert(new Position { Id = command.First }),
            Storage.Insert(new Position { Id = command.Second }));
    }
}

public static class CreateTeamAndPlayerHandler
{
    public static (IStorageAction<Team>, IStorageAction<Player>) Handle(CreateTeamAndPlayer command)
    {
        return (Storage.Insert(new Team { Id = command.Id, Name = command.TeamName }),
            Storage.Insert(new Player { Id = command.Id, Name = command.PlayerName }));
    }
}