using System.Data.Common;
using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharedPersistenceModels.Items;
using SharedPersistenceModels.Orders;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests.MultiTenancy;

/// <summary>
/// E3 (durable compliance rig). With a database per tenant, the EF Core middleware commits the tenant DbContext through
/// <c>CommitTenantedDbContextTransaction</c>, which never called <c>EfCoreEnvelopeTransaction.CommitAsync</c> -- the
/// one place the durable inbox row is marked Handled inside the handler's transaction. The mark was left to the
/// durable receiver after the commit, so even an ordinary shutdown between the two ran the message again. The tenant
/// path now commits exactly as the single-database path does.
/// </summary>
public class tenanted_commit_marks_the_inbox_row_inside_the_transaction : MultiTenancyCompliance
{
    private const string SchemaName = "tenanted_inbox_mark";

    public tenanted_commit_marks_the_inbox_row_inside_the_transaction() : base(DatabaseEngine.PostgreSQL)
    {
        InboxStatusAtCommit.Reset();
    }

    public override void Configure(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        // Same as the other managed multi-tenancy tests: the lightweight saga persistence needs this gone
        opts.Services.RemoveAll(typeof(OrdersDbContext));
        opts.AddSagaType<Order>();

        opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>((builder, connectionString, _) =>
        {
            builder.UseNpgsql(connectionString.Value, b => b.MigrationsAssembly("MultiTenantedEfCoreWithPostgreSQL"))
                .AddInterceptors(new InboxStatusAtCommit());
        }, AutoCreate.CreateOrUpdate);

        opts.Discovery.IncludeType(typeof(AddTenantedItemHandler));
    }

    [Fact]
    public async Task the_inbox_row_is_handled_by_the_time_the_tenant_transaction_commits()
    {
        var id = Guid.NewGuid();

        // Sent, not invoked: the durable local queue is what gives the message an inbox row in the tenant's database
        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new AddTenantedItem(id), new DeliveryOptions { TenantId = "blue" });

        InboxStatusAtCommit.Seen.ShouldBe(EnvelopeStatus.Handled.ToString());
    }

    /// <summary>
    /// Reads the handler's own inbox row through the committing transaction, just before it commits
    /// </summary>
    private class InboxStatusAtCommit : DbTransactionInterceptor
    {
        public static volatile string? Seen;
        public static Guid EnvelopeId;

        public static void Reset()
        {
            Seen = null;
            EnvelopeId = Guid.Empty;
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (EnvelopeId == Guid.Empty) return result;

            await using var cmd = transaction.Connection!.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"select status from {SchemaName}.{DatabaseConstants.IncomingTable} where id = @id";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = EnvelopeId;
            cmd.Parameters.Add(parameter);

            Seen = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
            return result;
        }
    }

    public record AddTenantedItem(Guid Id);

    public static class AddTenantedItemHandler
    {
        public static void Handle(AddTenantedItem command, ItemsDbContext dbContext, Envelope envelope)
        {
            InboxStatusAtCommit.EnvelopeId = envelope.Id;
            dbContext.Items.Add(new Item { Id = command.Id, Name = "tenanted" });
        }
    }
}
