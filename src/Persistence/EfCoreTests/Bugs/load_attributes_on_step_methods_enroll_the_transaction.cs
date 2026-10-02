using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.Bugs;

[Collection("sqlserver")]
public class load_attributes_on_step_methods_enroll_the_transaction : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RenameInBeforeHandler))
                    .IncludeType(typeof(RenameInBeforeTransactionalHandler))
                    .IncludeType(typeof(RenameAllInValidateHandler))
                    .IncludeType(typeof(RenameFirstOrDefaultInBeforeHandler))
                    .IncludeType(typeof(RenameQueryableInBeforeHandler))
                    .IncludeType(typeof(RenameSpecificationInBeforeHandler))
                    .IncludeType(typeof(RenameAllInLoadHandler));

                opts.Services.AddDbContextWithWolverineIntegration<RenamerDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "step_load_attributes");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> seedAsync()
    {
        var id = Guid.NewGuid();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RenamerDbContext>();
        db.Items.RemoveRange(db.Items);
        db.Items.Add(new RenamerItem { Id = id, Name = "original" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    private async Task<string> nameOfAsync(Guid id)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RenamerDbContext>();
        var item = await db.Items.AsNoTracking()
            .SingleAsync(x => x.Id == id, TestContext.Current.CancellationToken);
        return item.Name;
    }

    [Theory]
    [InlineData(typeof(RenameInBefore))]
    [InlineData(typeof(RenameInBeforeTransactional))]
    [InlineData(typeof(RenameAllInValidate))]
    [InlineData(typeof(RenameFirstOrDefaultInBefore))]
    [InlineData(typeof(RenameQueryableInBefore))]
    [InlineData(typeof(RenameSpecificationInBefore))]
    [InlineData(typeof(RenameAllInLoad))]
    public void a_load_attribute_on_a_step_makes_the_chain_transactional(Type messageType)
    {
        _host.GetRuntime().Handlers.HandlerFor(messageType);
        var chain = _host.GetRuntime().Handlers.ChainFor(messageType);
        chain.ShouldNotBeNull();

        var code = chain.SourceCode.ShouldNotBeNull();

        code.Contains("SaveChangesAsync").ShouldBeTrue(
            $"Loading through a step's attribute counts as using the DbContext, so the transactional middleware has to apply. Generated source:\n{code}");
    }

    [Fact]
    public async Task entity_on_a_before_parameter_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameInBefore(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task entity_on_a_before_parameter_with_explicit_transactional_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameInBeforeTransactional(id));

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Fact]
    public async Task all_on_a_validate_parameter_saves_the_mutation()
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(new RenameAllInValidate());

        (await nameOfAsync(id)).ShouldBe("renamed");
    }

    [Theory]
    [InlineData(typeof(RenameFirstOrDefaultInBefore))]
    [InlineData(typeof(RenameQueryableInBefore))]
    [InlineData(typeof(RenameSpecificationInBefore))]
    [InlineData(typeof(RenameAllInLoad))]
    public async Task a_load_attribute_on_a_step_saves_the_mutation(Type messageType)
    {
        var id = await seedAsync();

        await _host.InvokeMessageAndWaitAsync(Activator.CreateInstance(messageType)!);

        (await nameOfAsync(id)).ShouldBe("renamed");
    }
}

public record RenameInBefore(Guid Id);

public record RenameInBeforeTransactional(Guid Id);

public record RenameAllInValidate;

public record RenameFirstOrDefaultInBefore;

public record RenameQueryableInBefore;

public record RenameSpecificationInBefore;

public record RenameAllInLoad;

// [WolverineIgnore]: other hosts in this assembly use conventional discovery and don't map RenamerItem
[WolverineIgnore]
public static class RenameInBeforeHandler
{
    public static void Before([Entity] RenamerItem item)
    {
    }

    public static void Handle(RenameInBefore cmd, RenamerItem item) => item.Name = "renamed";
}

[WolverineIgnore]
public static class RenameInBeforeTransactionalHandler
{
    public static void Before([Entity] RenamerItem item)
    {
    }

    [Transactional]
    public static void Handle(RenameInBeforeTransactional cmd, RenamerItem item) => item.Name = "renamed";
}

[WolverineIgnore]
public static class RenameAllInValidateHandler
{
    public static void Validate([All] IReadOnlyList<RenamerItem> items)
    {
        foreach (var item in items) item.Name = "renamed";
    }

    public static void Handle(RenameAllInValidate cmd)
    {
    }
}

[WolverineIgnore]
public static class RenameFirstOrDefaultInBeforeHandler
{
    public static void Before([FirstOrDefault] RenamerItem? item)
    {
        if (item != null) item.Name = "renamed";
    }

    public static void Handle(RenameFirstOrDefaultInBefore cmd)
    {
    }
}

[WolverineIgnore]
public static class RenameQueryableInBeforeHandler
{
    public static async Task Before([Queryable] IQueryable<RenamerItem> items)
    {
        foreach (var item in await items.ToListAsync()) item.Name = "renamed";
    }

    public static void Handle(RenameQueryableInBefore cmd)
    {
    }
}

[WolverineIgnore]
public static class RenameSpecificationInBeforeHandler
{
    public static void Before([FromQuerySpecification(typeof(AllRenamerItems))] IReadOnlyList<RenamerItem> items)
    {
        foreach (var item in items) item.Name = "renamed";
    }

    public static void Handle(RenameSpecificationInBefore cmd)
    {
    }
}

[WolverineIgnore]
public static class RenameAllInLoadHandler
{
    public static void Load([All] IReadOnlyList<RenamerItem> items)
    {
        foreach (var item in items) item.Name = "renamed";
    }

    public static void Handle(RenameAllInLoad cmd)
    {
    }
}
