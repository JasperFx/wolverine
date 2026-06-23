using Microsoft.EntityFrameworkCore;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;

namespace EfCoreTests;

public class EfCoreStorageActionApplierTests
{
    private static TodoDbContext InMemoryContext() =>
        new(new DbContextOptionsBuilder<TodoDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task update_of_tracked_entity_only_marks_changed_properties()
    {
        await using var context = InMemoryContext();

        var todo = new Todo { Id = "1", Name = "original", IsComplete = false };
        context.Attach(todo);
        todo.Name = "changed";

        await EfCoreStorageActionApplier.ApplyAction(context, Storage.Update(todo));

        var entry = context.Entry(todo);
        entry.State.ShouldBe(EntityState.Modified);
        entry.Property(x => x.Name).IsModified.ShouldBeTrue();
        entry.Property(x => x.IsComplete).IsModified.ShouldBeFalse();
    }

    [Fact]
    public async Task update_of_detached_entity_still_marks_all_properties_modified()
    {
        await using var context = InMemoryContext();

        var todo = new Todo { Id = "1", Name = "original", IsComplete = false };

        await EfCoreStorageActionApplier.ApplyAction(context, Storage.Update(todo));

        var entry = context.Entry(todo);
        entry.State.ShouldBe(EntityState.Modified);
        entry.Property(x => x.Name).IsModified.ShouldBeTrue();
        entry.Property(x => x.IsComplete).IsModified.ShouldBeTrue();
    }
}