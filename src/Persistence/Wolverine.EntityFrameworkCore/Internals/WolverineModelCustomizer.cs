using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Wolverine.RDBMS;

namespace Wolverine.EntityFrameworkCore.Internals;

public class WolverineModelCustomizer : RelationalModelCustomizer
{
    public WolverineModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        var settings = context.Database.GetService<DatabaseSettings>();

        modelBuilder.MapWolverineEnvelopeStorage(settings.SchemaName);

        markSagaIdentifiersAsApplicationAssigned(modelBuilder);
    }

    // Wolverine assigns saga identities application-side (the id travels on the
    // incoming message), so a saga key must never be a database-generated identity
    // column. EF's convention makes int/long keys ValueGenerated.OnAdd (identity),
    // which breaks saga inserts as soon as the table DDL honors the model (EF-managed
    // migrations, or Weasel >= 9.18's EF-model translation — weasel#382). Only
    // convention-sourced value generation is overridden; an explicit
    // ValueGeneratedOnAdd() by the user is left alone
    private static void markSagaIdentifiersAsApplicationAssigned(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!entityType.ClrType.CanBeCastTo<Saga>())
            {
                continue;
            }

            var primaryKey = entityType.FindPrimaryKey();
            if (primaryKey == null)
            {
                continue;
            }

            foreach (var property in primaryKey.Properties)
            {
                if (((IConventionProperty)property).GetValueGeneratedConfigurationSource() !=
                    ConfigurationSource.Explicit)
                {
                    property.ValueGenerated = ValueGenerated.Never;
                }
            }
        }
    }
}

