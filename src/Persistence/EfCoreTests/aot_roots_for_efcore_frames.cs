using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace EfCoreTests;

public record TenantedItemCommand(Guid Id, string Name);

// [WolverineIgnore] because conventional discovery in this test project would otherwise hand this
// handler -- and its multi-tenanted ItemsDbContext dependency -- to every other host in the assembly.
[WolverineIgnore]
public static class TenantedItemCommandHandler
{
    public static void Handle(TenantedItemCommand command, ItemsDbContext db)
    {
        db.Items.Add(new Item { Id = command.Id, Name = command.Name });
    }
}

/// <summary>
///     GH-4765. The EF Core frame provider closes two generic frames over the user's own
///     <c>DbContext</c> type, and nothing else in a published app names either closed type — so ILC trims
///     them and <c>CloseAndBuildAs</c> throws while the chain model is built, which happens at startup
///     under <c>TypeLoadMode.Static</c> as well. Both frames name their closed types through
///     <see cref="IAotRootSource" /> so the emitted rooting block keeps them alive.
/// </summary>
public class aot_roots_for_efcore_frames
{
    [Fact]
    public void the_tenanted_dbcontext_frame_is_rooted_in_the_generated_registry()
    {
        // The rooting block is only emitted while actually generating code -- same guard as the
        // message-type scan, and the same one CoreTests' aot_root_contribution uses.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var code = generateAllCode();

            // Managed multi-tenancy puts CreateTenantedDbContext<ItemsDbContext> in the chain's
            // middleware; the open generic could never be named in a [DynamicDependency] at all.
            code.ShouldContain(
                "typeof(global::Wolverine.EntityFrameworkCore.Internals.CreateTenantedDbContext<SharedPersistenceModels.Items.ItemsDbContext>)");

            // ...and the interface the frame resolves BuildAndEnrollAsync off, which isMultiTenanted also
            // reaches through MakeGenericType.
            code.ShouldContain(
                "typeof(global::Wolverine.EntityFrameworkCore.Internals.IDbContextBuilder<SharedPersistenceModels.Items.ItemsDbContext>)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void the_tenanted_dbcontext_frame_really_is_in_the_middleware_the_root_walk_sees()
    {
        // The roots are collected off Middleware/Postprocessors/PostCommitPostprocessors. A frame the
        // provider inserted somewhere else would silently contribute nothing, and the assertion above
        // would then only be telling us the frame's own AotRoots() method compiles.
        using var host = buildHost();

        // Resolving the code file collections is what compiles the handler graph; without it there are no
        // chains to look at.
        _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

        var chain = host.Services.GetRequiredService<HandlerGraph>()
            .ChainFor(typeof(TenantedItemCommand)).ShouldNotBeNull();

        chain.Middleware.OfType<IAotRootSource>()
            .SelectMany(x => x.AotRoots())
            .ShouldContain(typeof(CreateTenantedDbContext<ItemsDbContext>));
    }

    [Fact]
    public void the_ancillary_store_frame_names_both_types_it_needs()
    {
        // The frame itself, because the provider closes it reflectively, and
        // AncillaryMessageStoreApplication<T> because MethodCall resolves Apply off it BY NAME -- the
        // generated code's direct call keeps the body but not the metadata GetMethod needs.
        //
        // Asserted directly rather than through generated code: reaching the frame at all needs an
        // ancillary message store, and that needs a real database. Insertion into chain.Middleware is
        // covered by Bugs/Bug_DurableLocalQueue_ancillary_store_routing.
        new ApplyAncillaryStoreFrame<ItemsDbContext>().AotRoots()
            .ShouldBe([
                typeof(ApplyAncillaryStoreFrame<ItemsDbContext>),
                typeof(AncillaryMessageStoreApplication<ItemsDbContext>)
            ]);
    }

    [Fact]
    public void every_contributed_root_survives_the_registry_public_filter()
    {
        // A non-public type cannot be named in a typeof() in generated code, so the registry drops it
        // instead of emitting a file that will not compile. That filter is exactly why CreateTenantedDbContext<T>
        // had to stop being internal -- a root silently filtered out is indistinguishable from no root at all.
        Type[] roots =
        [
            typeof(CreateTenantedDbContext<ItemsDbContext>),
            typeof(IDbContextBuilder<ItemsDbContext>),
            typeof(ApplyAncillaryStoreFrame<ItemsDbContext>),
            typeof(AncillaryMessageStoreApplication<ItemsDbContext>)
        ];

        foreach (var root in roots)
        {
            root.IsPublic.ShouldBeTrue($"{root.FullName} would be filtered out of the rooting block");
        }
    }

    private static string generateAllCode()
    {
        using var host = buildHost();

        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        return builder.GenerateAllCode();
    }

    private static IHost buildHost()
    {
        // No database: AutoCreate.None plus the in-memory provider is enough for the frame provider to
        // see a multi-tenanted DbContext, which is all these assertions are about.
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>(
                    (builder, _, _) => builder.UseInMemoryDatabase("aot-roots-items"), AutoCreate.None);

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TenantedItemCommandHandler));
            })
            .Build();
    }
}
