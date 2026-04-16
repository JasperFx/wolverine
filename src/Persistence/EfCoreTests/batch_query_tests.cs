using IntegrationTests;
using JasperFx.CodeGeneration.Model;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.EntityFrameworkCore.Batching;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.SqlServer;

namespace EfCoreTests;

/// <summary>
/// Integration tests demonstrating Weasel's BatchedQuery API for combining multiple
/// EF Core SELECT queries into a single database round trip.  See also the
/// BatchedLoadEntityFrame / CreateBatchQueryFrame / ExecuteBatchQueryFrame codegen
/// infrastructure in Wolverine.EntityFrameworkCore.Codegen for generated-code equivalents.
/// </summary>
[Collection("sqlserver")]
public class batch_query_tests : IClassFixture<EFCorePersistenceContext>
{
    private readonly IHost _host;

    public batch_query_tests(EFCorePersistenceContext ctx)
    {
        _host = ctx.theHost;
    }

    private async Task<Guid> InsertItemAsync(string name)
    {
        var id = Guid.NewGuid();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
        db.Items.Add(new Item { Id = id, Name = name });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task load_two_entities_in_single_round_trip()
    {
        var id1 = await InsertItemAsync("Batch Load A");
        var id2 = await InsertItemAsync("Batch Load B");

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();

        // All three calls to batch.Query*/Scalar return Tasks that resolve
        // only after batch.ExecuteAsync() issues one DbBatch to the server.
        var batch = db.CreateBatchQuery();
        var item1Task = batch.QuerySingle(db.Items.Where(x => x.Id == id1));
        var item2Task = batch.QuerySingle(db.Items.Where(x => x.Id == id2));
        await batch.ExecuteAsync();

        var item1 = await item1Task;
        var item2 = await item2Task;

        item1.ShouldNotBeNull();
        item1.Name.ShouldBe("Batch Load A");
        item2.ShouldNotBeNull();
        item2.Name.ShouldBe("Batch Load B");
    }

    [Fact]
    public async Task load_list_of_entities_via_batch()
    {
        var prefix = Guid.NewGuid().ToString("N")[..8];
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            db.Items.AddRange(
                new Item { Id = Guid.NewGuid(), Name = $"{prefix}_list_0" },
                new Item { Id = Guid.NewGuid(), Name = $"{prefix}_list_1" },
                new Item { Id = Guid.NewGuid(), Name = $"{prefix}_list_2" });
            await db.SaveChangesAsync();
        }

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            var batch = db.CreateBatchQuery();
            var listTask = batch.Query(db.Items.Where(x => x.Name.StartsWith(prefix)));
            await batch.ExecuteAsync();

            var items = await listTask;
            items.Count.ShouldBe(3);
            items.All(x => x.Name.StartsWith(prefix)).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task mix_single_and_list_queries_in_same_batch()
    {
        var id1 = await InsertItemAsync("Mixed Single");
        var prefix = Guid.NewGuid().ToString("N")[..8];

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
            db.Items.AddRange(
                new Item { Id = Guid.NewGuid(), Name = $"{prefix}_a" },
                new Item { Id = Guid.NewGuid(), Name = $"{prefix}_b" });
            await db.SaveChangesAsync();
        }

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();

            var batch = db.CreateBatchQuery();
            var singleTask = batch.QuerySingle(db.Items.Where(x => x.Id == id1));
            var listTask = batch.Query(db.Items.Where(x => x.Name.StartsWith(prefix)));
            await batch.ExecuteAsync();

            var single = await singleTask;
            var list = await listTask;

            single.ShouldNotBeNull();
            single.Id.ShouldBe(id1);
            list.Count.ShouldBe(2);
        }
    }
}

/// <summary>
/// Unit tests for the BatchedLoadEntityFrame / CreateBatchQueryFrame /
/// ExecuteBatchQueryFrame codegen infrastructure introduced in #2478.
/// </summary>
public class batched_load_entity_frame_codegen_tests
{
    [Fact]
    public void batched_load_frame_creates_saga_and_task_variables()
    {
        var sagaIdVar = Variable.For<Guid>();
        var frame = new BatchedLoadEntityFrame(
            typeof(ItemsDbContext),
            typeof(Item),
            sagaIdVar,
            "Id");

        frame.Saga.VariableType.ShouldBe(typeof(Item));
        frame.SagaTask.VariableType.ShouldBe(typeof(Task<Item>));
    }

    [Fact]
    public void create_batch_query_frame_creates_batch_variable()
    {
        var frame = new CreateBatchQueryFrame(typeof(ItemsDbContext));
        frame.BatchQuery.VariableType.ShouldBe(typeof(BatchedQuery));
        frame.BatchQuery.Usage.ShouldBe("batchQuery");
    }

    [Fact]
    public void execute_batch_query_frame_accepts_multiple_load_frames()
    {
        var batchVar = new CreateBatchQueryFrame(typeof(ItemsDbContext)).BatchQuery;
        var idVar = Variable.For<Guid>();

        var load1 = new BatchedLoadEntityFrame(typeof(ItemsDbContext), typeof(Item), idVar, "Id");
        var load2 = new BatchedLoadEntityFrame(typeof(ItemsDbContext), typeof(Item), idVar, "Id");

        var execFrame = new ExecuteBatchQueryFrame(batchVar, [load1, load2]);
        execFrame.ShouldNotBeNull();
    }
}
