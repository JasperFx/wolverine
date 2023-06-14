using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Wolverine.EntityFrameworkCore.Internals;

public class WolverineModelCustomizer : RelationalModelCustomizer
{
    public WolverineModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        var customizationOptions = context.Database.GetService<WolverineDbContextCustomizationOptions>();
        
        modelBuilder.MapWolverineEnvelopeStorage(customizationOptions.DatabaseSchema);
    }
}

public class WolverineDbContextCustomizationOptions
{
    public string? DatabaseSchema { get; init; }

    public static WolverineDbContextCustomizationOptions Default => new WolverineDbContextCustomizationOptions { DatabaseSchema = null };
}
