using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests;

/// <summary>
///     The point of the explicit per-provider entity attributes, in one application: <c>[FromMarten]</c> and
///     <c>[FromEfCore]</c> resolve to the store they name, not to whichever registered provider would have claimed
///     the type first.
/// </summary>
/// <remarks>
///     <para>
///         The discriminating case is <see cref="Coupon" />, which lives in BOTH stores. A plain <c>[Entity]</c>
///         resolves it to EF Core, because the selective provider outranks Marten's catch-all
///         <c>CanPersist</c> (GH-3359). So a <c>[FromMarten] Coupon</c> that comes back holding the EF Core row is
///         precisely the failure a broken provider hook would produce, and it is the only shape of test that can
///         see it — a Marten-only document would resolve to Marten with or without the attribute.
///     </para>
///     <para>
///         Both attributes are also exercised on the SAME handler method, since nothing else in the codebase
///         proves that two different persistence providers can each contribute a load frame to one chain.
///     </para>
/// </remarks>
public class explicit_provider_entity_attributes : IAsyncLifetime
{
    private static readonly Guid TheCouponId = Guid.NewGuid();

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MixedStoreHandler));

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "explicit_providers";
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "explicit_providers")
                    .UseLightweightSessions();

                opts.Services.AddDbContext<CouponDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await seedSqlServer();
        await seedMarten();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task seedSqlServer()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CouponDbContext>();

        // Deterministic DDL rather than EnsureCreated, which is a no-op against a database that already exists
        await db.Database.ExecuteSqlRawAsync(
            "if object_id('dbo.explicit_provider_coupons') is not null drop table dbo.explicit_provider_coupons;",
            TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "create table dbo.explicit_provider_coupons (Id uniqueidentifier not null primary key, Source nvarchar(100) not null);",
            TestContext.Current.CancellationToken);

        db.Coupons.Add(new Coupon { Id = TheCouponId, Source = "sql server" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task seedMarten()
    {
        var store = _host.DocumentStore();
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Coupon));
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(PromoNote));

        await using var session = store.LightweightSession();
        session.Store(new Coupon { Id = TheCouponId, Source = "marten" });
        session.Store(new PromoNote { Id = TheCouponId, Text = "spring sale" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task plain_entity_resolves_a_shared_type_to_the_selective_provider()
    {
        // The baseline this feature exists to override. Not a bug -- just not always what you meant.
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadCouponImplicitly(TheCouponId));

        tracked.Sent.SingleMessage<CouponSource>().Source.ShouldBe("sql server");
    }

    [Fact]
    public async Task from_marten_overrides_that_and_reads_the_marten_copy()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadCouponFromMarten(TheCouponId));

        tracked.Sent.SingleMessage<CouponSource>().Source.ShouldBe("marten");
    }

    [Fact]
    public async Task from_ef_core_names_the_store_it_would_have_got_anyway()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadCouponFromEfCore(TheCouponId));

        tracked.Sent.SingleMessage<CouponSource>().Source.ShouldBe("sql server");
    }

    [Fact]
    public async Task one_handler_can_load_from_both_stores_at_once()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadFromBothStores(TheCouponId));

        var read = tracked.Sent.SingleMessage<BothStoresRead>();

        // Same type, same identity, one handler, two stores, two answers
        read.MartenSource.ShouldBe("marten");
        read.EfCoreSource.ShouldBe("sql server");

        // ...and a Marten-only document alongside them, to prove the two load frames coexist rather than
        // one of them quietly winning the whole chain
        read.NoteText.ShouldBe("spring sale");
    }
}

/// <summary>
///     Lives in Marten AND in the EF Core DbContext below. That overlap is what makes this suite meaningful.
/// </summary>
public class Coupon
{
    public Guid Id { get; set; }
    public string Source { get; set; } = null!;
}

/// <summary>
///     Deliberately NOT mapped in any DbContext, so only Marten can claim it.
/// </summary>
public class PromoNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = null!;
}

public class CouponDbContext : DbContext
{
    public CouponDbContext(DbContextOptions<CouponDbContext> options) : base(options)
    {
    }

    public DbSet<Coupon> Coupons { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Coupon>(map =>
        {
            map.ToTable("explicit_provider_coupons");
            map.HasKey(x => x.Id);
            map.Property(x => x.Source);
        });
    }
}

public record ReadCouponImplicitly(Guid Id);

public record ReadCouponFromMarten(Guid Id);

public record ReadCouponFromEfCore(Guid Id);

public record ReadFromBothStores(Guid Id);

public record CouponSource(string Source);

public record BothStoresRead(string MartenSource, string EfCoreSource, string NoteText);

[WolverineIgnore]
public static class MixedStoreHandler
{
    public static CouponSource Handle(ReadCouponImplicitly command, [Entity] Coupon coupon)
        => new(coupon.Source);

    public static CouponSource Handle(ReadCouponFromMarten command, [FromMarten] Coupon coupon)
        => new(coupon.Source);

    public static CouponSource Handle(ReadCouponFromEfCore command, [FromEfCore] Coupon coupon)
        => new(coupon.Source);

    public static BothStoresRead Handle(ReadFromBothStores command,
        [FromMarten] Coupon martenCoupon,
        [FromEfCore] Coupon efCoreCoupon,
        [FromMarten] PromoNote note)
        => new(martenCoupon.Source, efCoreCoupon.Source, note.Text);

    public static void Handle(CouponSource message)
    {
    }

    public static void Handle(BothStoresRead message)
    {
    }
}
