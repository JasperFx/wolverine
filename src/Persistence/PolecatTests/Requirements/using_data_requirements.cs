using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Wolverine.Polecat.Requirements;
using Wolverine.Tracking;

namespace PolecatTests.Requirements;

public class using_data_requirements : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "data_requirements";
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        await using var session = _store.LightweightSession();
        session.DeleteWhere<PcThingCategory>(x => true);
        session.DeleteWhere<PcThing>(x => true);
        await session.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    #region Single IPolecatDataRequirement — MustExist

    [Fact]
    public async Task single_requirement_must_exist_happy_path()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "widgets" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreatePcThing("widget-1", "widgets"));

        await using var verify = _store.LightweightSession();
        var thing = await verify.LoadAsync<PcThing>("widget-1");
        thing.ShouldNotBeNull();
        thing!.CategoryId.ShouldBe("widgets");
    }

    [Fact]
    public async Task single_requirement_must_exist_sad_path()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThing("widget-2", "nonexistent"));
        });
    }

    #endregion

    #region IEnumerable<IPolecatDataRequirement> — MustExist + MustNotExist (batched)

    [Fact]
    public async Task enumerable_requirements_happy_path()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "gadgets" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreatePcThing2("gadget-1", "gadgets"));

        await using var verify = _store.LightweightSession();
        var thing = await verify.LoadAsync<PcThing>("gadget-1");
        thing.ShouldNotBeNull();
        thing!.CategoryId.ShouldBe("gadgets");
    }

    [Fact]
    public async Task enumerable_requirements_sad_path_category_missing()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThing2("gadget-2", "nonexistent"));
        });
    }

    [Fact]
    public async Task enumerable_requirements_sad_path_thing_already_exists()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "dupes" });
            session.Store(new PcThing { Id = "existing-thing", CategoryId = "dupes" });
            await session.SaveChangesAsync();
        }

        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThing2("existing-thing", "dupes"));
        });
    }

    #endregion

    #region [DocumentExists] declarative attribute — convention

    [Fact]
    public async Task document_exists_attribute_happy_path()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "attr-cat" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreatePcThingByAttribute("attr-thing", "attr-cat"));

        await using var verify = _store.LightweightSession();
        var thing = await verify.LoadAsync<PcThing>("attr-thing");
        thing.ShouldNotBeNull();
        thing!.CategoryId.ShouldBe("attr-cat");
    }

    [Fact]
    public async Task document_exists_attribute_sad_path()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThingByAttribute("attr-thing-2", "nonexistent"));
        });
    }

    #endregion

    #region [DocumentExists] declarative attribute — explicit property name

    [Fact]
    public async Task document_exists_attribute_explicit_happy_path()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "explicit-cat" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreatePcThingByAttributeExplicit("explicit-thing", "explicit-cat"));

        await using var verify = _store.LightweightSession();
        var thing = await verify.LoadAsync<PcThing>("explicit-thing");
        thing.ShouldNotBeNull();
        thing!.CategoryId.ShouldBe("explicit-cat");
    }

    [Fact]
    public async Task document_exists_attribute_explicit_sad_path()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThingByAttributeExplicit("explicit-thing-2", "nonexistent"));
        });
    }

    #endregion

    #region [DocumentDoesNotExist] declarative attribute

    [Fact]
    public async Task document_does_not_exist_attribute_happy_path()
    {
        await _host.InvokeMessageAndWaitAsync(new EnsureNoDuplicatePcThing("brand-new-thing"));
    }

    [Fact]
    public async Task document_does_not_exist_attribute_sad_path()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThing { Id = "already-here", CategoryId = "whatever" });
            await session.SaveChangesAsync();
        }

        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new EnsureNoDuplicatePcThing("already-here"));
        });
    }

    #endregion

    #region Stacked declarative attributes — exercises batch query optimization

    [Fact]
    public async Task stacked_attributes_happy_path()
    {
        // Category exists, target thing-id is free → both checks pass and the handler stores.
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "stacked-cat" });
            await session.SaveChangesAsync();
        }

        await _host.InvokeMessageAndWaitAsync(new CreatePcThingStacked("stacked-thing", "stacked-cat"));

        await using var verify = _store.LightweightSession();
        var thing = await verify.LoadAsync<PcThing>("stacked-thing");
        thing.ShouldNotBeNull();
    }

    [Fact]
    public async Task stacked_attributes_sad_path_category_missing()
    {
        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThingStacked("stacked-thing-2", "nonexistent"));
        });
    }

    [Fact]
    public async Task stacked_attributes_sad_path_thing_already_exists()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Store(new PcThingCategory { Id = "stacked-dupes" });
            session.Store(new PcThing { Id = "stacked-existing", CategoryId = "stacked-dupes" });
            await session.SaveChangesAsync();
        }

        await Should.ThrowAsync<RequiredDataMissingException>(async () =>
        {
            await _host.InvokeAsync(new CreatePcThingStacked("stacked-existing", "stacked-dupes"));
        });
    }

    #endregion
}

public record CreatePcThing(string Name, string Category);
public record CreatePcThing2(string Name, string Category);
public record CreatePcThingByAttribute(string Name, string PcThingCategoryId);
public record CreatePcThingByAttributeExplicit(string Name, string CategoryKey);
public record EnsureNoDuplicatePcThing(string PcThingId);
public record CreatePcThingStacked(string Name, string PcThingCategoryId);

public class PcThingCategory
{
    public string Id { get; set; } = "";
}

public class PcThing
{
    public string Id { get; set; } = "";
    public string CategoryId { get; set; } = "";
}

public static class CreatePcThingHandler
{
    public static IPolecatDataRequirement Before(CreatePcThing command)
        => PolecatOps.Document<PcThingCategory>().MustExist(command.Category);

    public static IPolecatOp Handle(CreatePcThing command) =>
        PolecatOps.Store(new PcThing { Id = command.Name, CategoryId = command.Category });
}

public static class CreatePcThing2Handler
{
    public static IEnumerable<IPolecatDataRequirement> Before(CreatePcThing2 command)
    {
        yield return PolecatOps.Document<PcThingCategory>().MustExist(command.Category);
        yield return PolecatOps.Document<PcThing>().MustNotExist(command.Name);
    }

    public static IPolecatOp Handle(CreatePcThing2 command) =>
        PolecatOps.Store(new PcThing { Id = command.Name, CategoryId = command.Category });
}

public static class CreatePcThingByAttributeHandler
{
    // Convention: looks for PcThingCategoryId on the command
    [DocumentExists<PcThingCategory>]
    public static IPolecatOp Handle(CreatePcThingByAttribute command) =>
        PolecatOps.Store(new PcThing { Id = command.Name, CategoryId = command.PcThingCategoryId });
}

public static class CreatePcThingByAttributeExplicitHandler
{
    // Explicit property name override
    [DocumentExists<PcThingCategory>(nameof(CreatePcThingByAttributeExplicit.CategoryKey))]
    public static IPolecatOp Handle(CreatePcThingByAttributeExplicit command) =>
        PolecatOps.Store(new PcThing { Id = command.Name, CategoryId = command.CategoryKey });
}

public static class EnsureNoDuplicatePcThingHandler
{
    [DocumentDoesNotExist<PcThing>]
    public static void Handle(EnsureNoDuplicatePcThing command)
    {
        // No-op — we're just exercising the existence check.
    }
}

public static class CreatePcThingStackedHandler
{
    // Two declarative attributes on the same handler exercise the batch query optimization:
    // the PolecatBatchingPolicy folds both CheckExistsAsync calls into a single IBatchedQuery.
    [DocumentExists<PcThingCategory>]
    [DocumentDoesNotExist<PcThing>(nameof(CreatePcThingStacked.Name))]
    public static IPolecatOp Handle(CreatePcThingStacked command) =>
        PolecatOps.Store(new PcThing { Id = command.Name, CategoryId = command.PcThingCategoryId });
}
