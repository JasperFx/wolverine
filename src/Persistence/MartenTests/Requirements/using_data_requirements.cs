using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Requirements;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests.Requirements;

public class using_data_requirements : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(ThingCategory));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Thing));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    #region Single IMartenDataRequirement - MustExist

    [Fact]
    public async Task single_requirement_must_exist_happy_path()
    {
        // Arrange: category exists
        using (var session = _store.LightweightSession())
        {
            session.Store(new ThingCategory { Id = "widgets" });
            await session.SaveChangesAsync();
        }

        // Act
        await _host.InvokeMessageAndWaitAsync(new CreateThing("widget-1", "widgets"));

        // Assert: Thing was created
        using (var session = _store.LightweightSession())
        {
            var thing = await session.LoadAsync<Thing>("widget-1");
            thing.ShouldNotBeNull();
            thing.CategoryId.ShouldBe("widgets");
        }
    }

    [Fact]
    public async Task single_requirement_must_exist_sad_path()
    {
        // Category does not exist - should throw
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreateThing("widget-2", "nonexistent"));
        });
    }

    #endregion

    #region IEnumerable<IMartenDataRequirement> - MustExist + MustNotExist

    [Fact]
    public async Task enumerable_requirements_happy_path()
    {
        // Arrange: category exists, thing does not
        using (var session = _store.LightweightSession())
        {
            session.Store(new ThingCategory { Id = "gadgets" });
            await session.SaveChangesAsync();
        }

        // Act
        await _host.InvokeMessageAndWaitAsync(new CreateThing2("gadget-1", "gadgets"));

        // Assert: Thing was created
        using (var session = _store.LightweightSession())
        {
            var thing = await session.LoadAsync<Thing>("gadget-1");
            thing.ShouldNotBeNull();
            thing.CategoryId.ShouldBe("gadgets");
        }
    }

    [Fact]
    public async Task enumerable_requirements_sad_path_category_missing()
    {
        // Category doesn't exist
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreateThing2("gadget-2", "nonexistent"));
        });
    }

    [Fact]
    public async Task enumerable_requirements_sad_path_thing_already_exists()
    {
        // Arrange: category exists AND thing already exists
        using (var session = _store.LightweightSession())
        {
            session.Store(new ThingCategory { Id = "dupes" });
            session.Store(new Thing { Id = "existing-thing", CategoryId = "dupes" });
            await session.SaveChangesAsync();
        }

        // MustNotExist should fail because thing already exists
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreateThing2("existing-thing", "dupes"));
        });
    }

    #endregion

    #region Single requirement + [Entity(Required = true)]

    [Fact]
    public async Task requirement_with_entity_attribute_happy_path()
    {
        // Arrange: category exists
        using (var session = _store.LightweightSession())
        {
            session.Store(new ThingCategory { Id = "tools" });
            await session.SaveChangesAsync();
        }

        // Act
        await _host.InvokeMessageAndWaitAsync(new CreateThing3("tool-1", "tools"));

        // Assert: Thing was created
        using (var session = _store.LightweightSession())
        {
            var thing = await session.LoadAsync<Thing>("tool-1");
            thing.ShouldNotBeNull();
            thing.CategoryId.ShouldBe("tools");
        }
    }

    [Fact]
    public async Task requirement_with_entity_attribute_sad_path()
    {
        // Category doesn't exist - requirement check should fail
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreateThing3("tool-2", "nonexistent"));
        });
    }

    #endregion

    #region [DocumentExists] attribute - convention

    [Fact]
    public async Task document_exists_attribute_happy_path()
    {
        using (var session = _store.LightweightSession())
        {
            session.Store(new ThingCategory { Id = "attr-cat" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreateThingByAttribute("attr-thing", "attr-cat"));

        using (var session = _store.LightweightSession())
        {
            var thing = await session.LoadAsync<Thing>("attr-thing");
            thing.ShouldNotBeNull();
            thing.CategoryId.ShouldBe("attr-cat");
        }
    }

    [Fact]
    public async Task document_exists_attribute_sad_path()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreateThingByAttribute("attr-thing-2", "nonexistent"));
        });
    }

    #endregion

    #region [DocumentExists] attribute - explicit property name

    [Fact]
    public async Task document_exists_attribute_explicit_happy_path()
    {
        using (var session = _store.LightweightSession())
        {
            session.Store(new ThingCategory { Id = "explicit-cat" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreateThingByAttributeExplicit("explicit-thing", "explicit-cat"));

        using (var session = _store.LightweightSession())
        {
            var thing = await session.LoadAsync<Thing>("explicit-thing");
            thing.ShouldNotBeNull();
            thing.CategoryId.ShouldBe("explicit-cat");
        }
    }

    [Fact]
    public async Task document_exists_attribute_explicit_sad_path()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreateThingByAttributeExplicit("explicit-thing-2", "nonexistent"));
        });
    }

    #endregion

    #region [DocumentDoesNotExist] attribute

    [Fact]
    public async Task document_does_not_exist_attribute_happy_path()
    {
        // Thing does not exist - should succeed
        await _host.InvokeMessageAndWaitAsync(new EnsureNoDuplicateThing("brand-new-thing"));
    }

    [Fact]
    public async Task document_does_not_exist_attribute_sad_path()
    {
        using (var session = _store.LightweightSession())
        {
            session.Store(new Thing { Id = "already-here", CategoryId = "whatever" });
            await session.SaveChangesAsync();
        }

        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new EnsureNoDuplicateThing("already-here"));
        });
    }

    #endregion
}

public record CreateThing(string Name, string Category);
public record CreateThing2(string Name, string Category);
public record CreateThing3(string Name, string Category);
public record CreateThingByAttribute(string Name, string ThingCategoryId);
public record CreateThingByAttributeExplicit(string Name, string CategoryKey);
public record EnsureNoDuplicateThing(string ThingId);

public class ThingCategory
{
    public required string Id { get; init; }
}

public class Thing
{
    public required string Id { get; init; }
    public required string CategoryId { get; init; }
}

public static class CreateThingHandler
{
    public static IMartenDataRequirement Before(CreateThing command)
        => MartenOps.Document<ThingCategory>().MustExist(command.Category);

    public static IMartenOp Handle(CreateThing command)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.Category
        });
    }

}

public static class CreateThing2Handler
{
    public static IEnumerable<IMartenDataRequirement> Before(CreateThing2 command)
    {
        yield return MartenOps.Document<ThingCategory>().MustExist(command.Category);
        yield return MartenOps.Document<Thing>().MustNotExist(command.Name);
    }


    public static IMartenOp Handle(CreateThing2 command)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.Category
        });
    }
}

public static class CreateThingByAttributeHandler
{
    // Convention: looks for ThingCategoryId on the command
    [DocumentExists<ThingCategory>]
    public static IMartenOp Handle(CreateThingByAttribute command)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.ThingCategoryId
        });
    }
}

public static class CreateThingByAttributeExplicitHandler
{
    // Explicit property name
    [DocumentExists<ThingCategory>(nameof(CreateThingByAttributeExplicit.CategoryKey))]
    public static IMartenOp Handle(CreateThingByAttributeExplicit command)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.CategoryKey
        });
    }
}

public static class EnsureNoDuplicateThingHandler
{
    [DocumentDoesNotExist<Thing>]
    public static void Handle(EnsureNoDuplicateThing command)
    {
        // No-op - just verifying the check
    }
}

public static class CreateThing3Handler
{
    public static IMartenDataRequirement Before(CreateThing3 command)
        => MartenOps.Document<ThingCategory>().MustExist(command.Category);

    public static IMartenOp Handle(CreateThing3 command, 
        
        [Entity(nameof(CreateThing3.Category), Required = true)] ThingCategory category)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.Category
        });
    }

}
